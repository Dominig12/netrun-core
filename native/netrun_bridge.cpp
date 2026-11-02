// Ограниченный транспортный мост. Поток BYOND только копирует данные и проверяет очереди;
// socket I/O и владение сокетом полностью принадлежат одному фоновому worker.
#if !defined(_WIN32) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <string>
#include <utility>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#define NETRUN_EXPORT extern "C" __declspec(dllexport)
#else
#include <arpa/inet.h>
#include <cerrno>
#include <dlfcn.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
#define NETRUN_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
/// Абсолютные лимиты ABI; конфигурация соединения может только уменьшить их.
constexpr std::size_t MaxFrame = 1024 * 1024;
constexpr std::size_t MaxQueued = 16 * MaxFrame;
constexpr std::size_t MaxPoll = 8 * MaxFrame;
constexpr std::size_t MaxFrames = 128;

/// Состояние одного подключения. Ошибка не запускает автоматический retry.
enum class State { Closed, Connecting, Connected, Disconnected, Closing };
/// Безопасные причины отказа: здесь нет адресов, токенов или содержимого кадров.
enum class Error { None, ConnectFailed, IoError, PeerClosed, FrameSize, InvalidUtf8 };

/// Нативный mutex: вызывающий ABI никогда не ожидает его освобождения.
class Mutex {
    /// Платформенный mutex хранится внутри объекта и не принимает внешние handles.
#ifdef _WIN32
    CRITICAL_SECTION value_;
#else
    pthread_mutex_t value_ = PTHREAD_MUTEX_INITIALIZER;
#endif
public:
    /// Создаёт локальный mutex; он не является именованным системным объектом.
    Mutex() {
#ifdef _WIN32
        InitializeCriticalSection(&value_);
#endif
    }
    /// Разрушение допустимо только после завершения worker и всех ABI-вызовов.
    ~Mutex() {
#ifdef _WIN32
        DeleteCriticalSection(&value_);
#else
        pthread_mutex_destroy(&value_);
#endif
    }
    /// Проверяет mutex без ожидания; используется только на ABI-пути.
    bool try_lock() {
#ifdef _WIN32
        return TryEnterCriticalSection(&value_) != 0;
#else
        return pthread_mutex_trylock(&value_) == 0;
#endif
    }
    /// Worker может ждать только краткое изменение bounded очередей, не завершение socket I/O.
    void lock() {
#ifdef _WIN32
        EnterCriticalSection(&value_);
#else
        pthread_mutex_lock(&value_);
#endif
    }
    /// Освобождает ранее полученный mutex.
    void unlock() {
#ifdef _WIN32
        LeaveCriticalSection(&value_);
#else
        pthread_mutex_unlock(&value_);
#endif
    }
    Mutex(const Mutex&) = delete;
    Mutex& operator=(const Mutex&) = delete;
};

/// RAII-guard, который умеет отказаться от захвата вместо блокировки ABI-потока.
class Guard {
    /// null означает отказ try-lock; destructor тогда ничего не освобождает.
    Mutex* mutex_;
public:
    /// waiting=true разрешён только worker; все экспортированные функции передают false.
    explicit Guard(Mutex& mutex, bool waiting) : mutex_(&mutex) {
        if (waiting) mutex.lock();
        else if (!mutex.try_lock()) mutex_ = nullptr;
    }
    /// Освобождает mutex только после успешного захвата.
    ~Guard() { if (mutex_) mutex_->unlock(); }
    /// Показывает, что блок данных можно безопасно прочитать или изменить.
    explicit operator bool() const { return mutex_ != nullptr; }
    Guard(const Guard&) = delete;
    Guard& operator=(const Guard&) = delete;
};

/// Состояние единственного соединения. Все payload принадлежат мосту, а не памяти DM.
struct Connection {
    /// Остановка проверяется не реже каждого 50-ms select; главному потоку не нужен join.
    std::atomic<bool> stop{false};
    /// Публикуется последним действием worker перед возвратом; после этого объект можно заменить.
    std::atomic<bool> exited{false};
    /// Защищает только очереди и их accounting.
    Mutex queues;
    /// Готовые к записи тела без бинарного header; inflight тело учитывается теми же счётчиками.
    std::deque<std::string> outbound;
    /// Полностью полученные тела; незавершённый вход также резервирует bytes/frames.
    std::deque<std::string> inbound;
    /// Суммарные квоты каждого направления, включая обрабатываемый worker кадр.
    std::size_t outbound_bytes = 0, inbound_bytes = 0, outbound_frames = 0, inbound_frames = 0;
    /// Настроенные пределы тела, очередей и количества кадров.
    std::size_t frame_limit, queue_limit, frame_count_limit;
    /// Только loopback IPv4 port; hostname и произвольный DNS не поддерживаются.
    std::uint16_t port;
    /// Фиксирует конфигурацию до запуска worker.
    Connection(std::uint16_t p, std::size_t frames, std::size_t bytes, std::size_t frame)
        : frame_limit(frame), queue_limit(bytes), frame_count_limit(frames), port(p) {}
};

/// Синглтон ABI: закрывающееся соединение не заменяется новым, пока worker не завершён.
struct Manager {
    /// Сериализует только ABI lifetime соединения; worker никогда не удерживает этот gate.
    Mutex gate;
    /// Единственный объект сохраняется до подтверждённого завершения worker.
    std::unique_ptr<Connection> connection;
    /// Атомарное состояние доступно даже при неудачном ABI try-lock.
    std::atomic<State> state{State::Closed};
    /// Последняя безопасная worker-причина без strerror, адресов и payload.
    std::atomic<Error> error{Error::None};
};

/// Процессный singleton намеренно не разрушает активный worker при C++ static teardown.
Manager& manager() { static Manager* value = new Manager(); return *value; }
/// Буфер возврата принадлежит библиотеке до следующего ABI-вызова на данном потоке.
thread_local std::string result_buffer;

/// Возвращает только фиксированное имя состояния.
const char* state_name(State state) {
    switch (state) {
    case State::Connecting: return "connecting";
    case State::Connected: return "connected";
    case State::Disconnected: return "disconnected";
    case State::Closing: return "closing";
    default: return "closed";
    }
}
/// Возвращает только безопасную причину worker-ошибки.
const char* error_name(Error error) {
    switch (error) {
    case Error::ConnectFailed: return "connect_failed";
    case Error::IoError: return "io_error";
    case Error::PeerClosed: return "peer_closed";
    case Error::FrameSize: return "frame_size";
    case Error::InvalidUtf8: return "invalid_utf8";
    default: return "ok";
    }
}
/// Формирует маленький ответ без данных пользовательского кадра.
char* reply(const char* code) {
    result_buffer = "{\"state\":\"";
    result_buffer += state_name(manager().state.load());
    result_buffer += "\",\"code\":\"";
    result_buffer += code;
    result_buffer += "\"}";
    return result_buffer.data();
}
/// Даже отказ poll сохраняет форму ответа с пустым массивом кадров.
char* poll_reply(const char* code) {
    reply(code);
    result_buffer.insert(1, "\"frames\":[],");
    return result_buffer.data();
}
/// Последний fallback не выделяет память и не пропускает исключение через C ABI.
char* exception_reply() noexcept {
    static char value[] = "{\"state\":\"closed\",\"code\":\"internal_error\"}";
    return value;
}
/// Ограниченно копирует C-string до возврата в DM; никаких argv pointers worker не сохраняет.
bool argument(unsigned int argc, char** argv, unsigned int index, std::size_t limit, std::string& output) {
    if (!argv || index >= argc || !argv[index]) return false;
    std::size_t length = 0;
    while (length <= limit && argv[index][length] != '\0') ++length;
    if (length > limit) return false;
    output.assign(argv[index], length);
    return true;
}
/// Читает canonical положительный ASCII integer без locale, знаков и переполнения.
bool number(const std::string& value, std::size_t minimum, std::size_t maximum, std::size_t& output) {
    if (value.empty() || value[0] < '1' || value[0] > '9') return false;
    std::size_t parsed = 0;
    for (unsigned char c : value) {
        if (c < '0' || c > '9' || parsed > (maximum - (c - '0')) / 10) return false;
        parsed = parsed * 10 + c - '0';
    }
    if (parsed < minimum || parsed > maximum) return false;
    output = parsed;
    return true;
}
/// Проверяет UTF-8, запрещая overlong, surrogate code points и значения выше U+10FFFF.
bool valid_utf8(const std::string& value) {
    for (std::size_t i = 0; i < value.size();) {
        const auto first = static_cast<unsigned char>(value[i++]);
        if (first < 0x80) continue;
        unsigned int code = 0, count = 0, minimum = 0;
        if (first >= 0xc2 && first <= 0xdf) { code = first & 31; count = 1; minimum = 0x80; }
        else if (first >= 0xe0 && first <= 0xef) { code = first & 15; count = 2; minimum = 0x800; }
        else if (first >= 0xf0 && first <= 0xf4) { code = first & 7; count = 3; minimum = 0x10000; }
        else return false;
        if (value.size() - i < count) return false;
        while (count--) {
            const auto next = static_cast<unsigned char>(value[i++]);
            if ((next & 0xc0) != 0x80) return false;
            code = (code << 6) | (next & 63);
        }
        if (code < minimum || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)) return false;
    }
    return true;
}
/// Вычисляет JSON string size без allocation; все исходные UTF-8 байты сохраняются.
std::size_t escaped_size(const std::string& value) {
    std::size_t size = 2;
    for (unsigned char c : value) size += c < 0x20 ? 6 : (c == '"' || c == '\\' ? 2 : 1);
    return size;
}
/// Добавляет корректную JSON string после предварительной проверки общего response budget.
void append_escaped(std::string& output, const std::string& value) {
    static constexpr char Hex[] = "0123456789abcdef";
    output += '"';
    for (unsigned char c : value) {
        if (c < 0x20) { output += "\\u00"; output += Hex[c >> 4]; output += Hex[c & 15]; }
        else { if (c == '"' || c == '\\') output += '\\'; output += static_cast<char>(c); }
    }
    output += '"';
}

#ifdef _WIN32
using Socket = SOCKET;
constexpr Socket InvalidSocket = INVALID_SOCKET;
/// Проверяет ожидаемое nonblocking состояние WinSock.
bool would_block() { const int e = WSAGetLastError(); return e == WSAEWOULDBLOCK || e == WSAEINPROGRESS; }
/// Закрывает сокет только на worker-потоке.
void socket_close(Socket socket) { shutdown(socket, SD_BOTH); closesocket(socket); }
#else
using Socket = int;
constexpr Socket InvalidSocket = -1;
/// Проверяет ожидаемое nonblocking состояние POSIX socket.
bool would_block() { return errno == EAGAIN || errno == EWOULDBLOCK || errno == EINPROGRESS || errno == EINTR; }
/// Закрывает сокет только на worker-потоке.
void socket_close(Socket socket) { shutdown(socket, SHUT_RDWR); close(socket); }
#endif

/// RAII владение сокетом; ABI-поток никогда не закрывает используемый worker descriptor.
struct OwnedSocket {
    /// Descriptor используется только worker и освобождается до публикации exited.
    Socket value = InvalidSocket;
    /// Shutdown выполняется асинхронно относительно DM, на owner-потоке сокета.
    ~OwnedSocket() { if (value != InvalidSocket) socket_close(value); }
};
/// Пауза применяется только worker при заполненной входной очереди и отсутствии исходящих кадров.
void idle_pause() {
#ifdef _WIN32
    Sleep(50);
#else
    const timespec delay{0, 50 * 1000 * 1000};
    nanosleep(&delay, nullptr);
#endif
}
/// Один bounded select; socket connect/read/write не вызывается на потоке BYOND.
int wait_socket(Socket socket, bool read, bool write, bool& readable, bool& writable) {
    if (!read && !write) { idle_pause(); readable = writable = false; return 0; }
    fd_set reads, writes;
    FD_ZERO(&reads); FD_ZERO(&writes);
    if (read) FD_SET(socket, &reads);
    if (write) FD_SET(socket, &writes);
    timeval timeout{0, 50 * 1000};
    const int ready = select(static_cast<int>(socket + 1), read ? &reads : nullptr, write ? &writes : nullptr, nullptr, &timeout);
    readable = ready > 0 && read && FD_ISSET(socket, &reads);
    writable = ready > 0 && write && FD_ISSET(socket, &writes);
    return ready;
}
/// Создаёт nonblocking loopback соединение с ограниченным физическим connect timeout.
bool connect_socket(Connection& connection, OwnedSocket& socket) {
    socket.value = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket.value == InvalidSocket) return false;
#ifdef _WIN32
    u_long enabled = 1;
    if (ioctlsocket(socket.value, FIONBIO, &enabled) != 0) return false;
#else
    if (socket.value >= FD_SETSIZE) return false;
    if (fcntl(socket.value, F_SETFL, fcntl(socket.value, F_GETFL) | O_NONBLOCK) != 0) return false;
#endif
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(connection.port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (::connect(socket.value, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0) return true;
    if (!would_block()) return false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (!connection.stop.load() && std::chrono::steady_clock::now() < deadline) {
        bool readable = false, writable = false;
        if (wait_socket(socket.value, false, true, readable, writable) < 0) return false;
        if (!writable) continue;
        int error = 0;
#ifdef _WIN32
        int length = sizeof(error);
#else
        socklen_t length = sizeof(error);
#endif
        return getsockopt(socket.value, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length) == 0 && error == 0;
    }
    return false;
}
/// Освобождает reservation незавершённого входа; принятые целые ответы доступны poll после disconnect.
void release_partial(Connection& connection, bool reserved, std::size_t bytes) {
    Guard guard(connection.queues, true);
    if (reserved) { connection.inbound_bytes -= bytes; --connection.inbound_frames; }
    connection.outbound.clear();
    connection.outbound_bytes = connection.outbound_frames = 0;
}
/// Выполняет partial reads/writes и ограничивает входную память до allocation тела.
Error exchange(Connection& connection, Socket socket) {
    std::array<unsigned char, 4> read_header{}, write_header{};
    std::size_t header_used = 0, receive_size = 0, received = 0, sent = 0;
    bool reserved = false;
    std::string incoming, outgoing;
    Error error = Error::None;
    while (!connection.stop.load()) {
        if (outgoing.empty()) {
            Guard guard(connection.queues, true);
            if (!connection.outbound.empty()) {
                outgoing = std::move(connection.outbound.front()); connection.outbound.pop_front(); sent = 0;
                const auto size = static_cast<std::uint32_t>(outgoing.size());
                for (int i = 0; i < 4; ++i) write_header[i] = static_cast<unsigned char>(size >> (24 - i * 8));
            }
        }
        if (header_used == 4 && !reserved) {
            Guard guard(connection.queues, true);
            if (connection.inbound_frames < connection.frame_count_limit && receive_size <= connection.queue_limit - connection.inbound_bytes) {
                ++connection.inbound_frames; connection.inbound_bytes += receive_size; reserved = true;
                incoming.resize(receive_size); received = 0;
            }
        }
        bool readable = false, writable = false;
        if (wait_socket(socket, header_used < 4 || reserved, !outgoing.empty(), readable, writable) < 0) {
            error = Error::IoError; break;
        }
        if (readable) {
            char* destination = header_used < 4 ? reinterpret_cast<char*>(read_header.data() + header_used) : incoming.data() + received;
            const std::size_t remaining = header_used < 4 ? 4 - header_used : receive_size - received;
            const int count = recv(socket, destination, static_cast<int>(remaining), 0);
            if (count == 0) { error = Error::PeerClosed; break; }
            if (count < 0) { if (!would_block()) { error = Error::IoError; break; } }
            else if (header_used < 4) {
                header_used += static_cast<std::size_t>(count);
                if (header_used == 4) {
                    receive_size = 0;
                    for (unsigned char byte : read_header) receive_size = (receive_size << 8) | byte;
                    if (receive_size == 0 || receive_size > connection.frame_limit) { error = Error::FrameSize; break; }
                }
            } else {
                received += static_cast<std::size_t>(count);
                if (received == receive_size) {
                    if (!valid_utf8(incoming)) { error = Error::InvalidUtf8; break; }
                    { Guard guard(connection.queues, true); connection.inbound.push_back(std::move(incoming)); }
                    reserved = false; header_used = 0; receive_size = received = 0;
                }
            }
        }
        if (writable) {
            const char* source = sent < 4 ? reinterpret_cast<const char*>(write_header.data() + sent) : outgoing.data() + sent - 4;
            const std::size_t remaining = sent < 4 ? 4 - sent : outgoing.size() + 4 - sent;
#ifdef _WIN32
            const int count = send(socket, source, static_cast<int>(remaining), 0);
#else
            const int count = static_cast<int>(send(socket, source, remaining, MSG_NOSIGNAL));
#endif
            if (count < 0) { if (!would_block()) { error = Error::IoError; break; } }
            else if (count == 0) { error = Error::IoError; break; }
            else {
                sent += static_cast<std::size_t>(count);
                if (sent == outgoing.size() + 4) {
                    Guard guard(connection.queues, true);
                    connection.outbound_bytes -= outgoing.size(); --connection.outbound_frames;
                    outgoing.clear();
                }
            }
        }
    }
    release_partial(connection, reserved, receive_size);
    return error;
}
/// Весь worker завершается без исключений; exited публикуется только после освобождения сокета.
void run_worker(Connection* connection) noexcept {
    Error error = Error::None;
#ifdef _WIN32
    WSADATA winsock{};
    const bool initialized = WSAStartup(MAKEWORD(2, 2), &winsock) == 0;
    if (!initialized) error = Error::ConnectFailed;
#endif
    try {
        OwnedSocket socket;
#ifdef _WIN32
        if (initialized)
#endif
        {
            if (!connect_socket(*connection, socket)) error = Error::ConnectFailed;
            else if (!connection->stop.load()) {
                State expected = State::Connecting;
                if (manager().state.compare_exchange_strong(expected, State::Connected))
                    error = exchange(*connection, socket.value);
            }
        }
    } catch (...) { error = Error::IoError; }
#ifdef _WIN32
    if (initialized) WSACleanup();
#endif
    manager().error.store(connection->stop.load() ? Error::None : error);
    manager().state.store(connection->stop.load() ? State::Closed : State::Disconnected);
    connection->exited.store(true, std::memory_order_release);
}
#ifdef _WIN32
/// Win32 thread trampoline не передаёт никаких DM references.
DWORD WINAPI worker_entry(void* value) { run_worker(static_cast<Connection*>(value)); return 0; }
#else
/// POSIX thread trampoline не передаёт никаких DM references.
void* worker_entry(void* value) { run_worker(static_cast<Connection*>(value)); return nullptr; }
#endif
/// Caller может выгрузить свой handle при shutdown; код/TLS detached worker закрепляются один раз до конца процесса.
bool keep_module_loaded() noexcept {
    static const bool pinned = []() noexcept {
        static const unsigned char module_address = 0;
#ifdef _WIN32
        HMODULE module = nullptr;
        return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&module_address), &module) != 0;
#else
        Dl_info module{};
        if (!dladdr(&module_address, &module) || !module.dli_fname) return false;
        // NOLOAD находит именно текущий ELF; NODELETE и единственная resident reference исключают dlclose race.
        // Handle намеренно не закрывается: Manager также принадлежит процессу, а не одному клиентскому binding.
        return dlopen(module.dli_fname, RTLD_NOW | RTLD_NOLOAD | RTLD_NODELETE) != nullptr;
#endif
    }();
    return pinned;
}
/// Запускает единственный detached worker; его lifetime контролирует exited, а не blocking join.
bool start_worker(Connection* connection) {
    if (!keep_module_loaded()) return false;
#ifdef _WIN32
    HANDLE thread = CreateThread(nullptr, 0, worker_entry, connection, 0, nullptr);
    if (!thread) return false;
    CloseHandle(thread); return true;
#else
    pthread_t thread;
    if (pthread_create(&thread, nullptr, worker_entry, connection) != 0) return false;
    pthread_detach(thread); return true;
#endif
}
} // namespace

/// open(host,port,max_frames,max_queued_bytes,max_frame_bytes): только один loopback worker.
NETRUN_EXPORT char* netrun_open(unsigned int argc, char** argv) noexcept {
    try {
        std::string host, port, frames, bytes, frame;
        std::size_t p, f, b, m;
        if (argc != 5 || !argument(argc, argv, 0, 16, host) || host != "127.0.0.1" ||
            !argument(argc, argv, 1, 5, port) || !number(port, 1, 65535, p) ||
            !argument(argc, argv, 2, 3, frames) || !number(frames, 1, MaxFrames, f) ||
            !argument(argc, argv, 3, 8, bytes) || !number(bytes, MaxFrame, MaxQueued, b) ||
            !argument(argc, argv, 4, 7, frame) || !number(frame, 1, MaxFrame, m)) return reply("invalid_arguments");
        Guard guard(manager().gate, false); if (!guard) return reply("would_block");
        if (manager().connection && (!manager().connection->exited.load(std::memory_order_acquire) || manager().state.load() != State::Closed))
            return reply("already_open");
        manager().connection = std::make_unique<Connection>(static_cast<std::uint16_t>(p), f, b, m);
        manager().error.store(Error::None); manager().state.store(State::Connecting);
        if (!start_worker(manager().connection.get())) { manager().connection->exited.store(true); manager().state.store(State::Closed); return reply("worker_start_failed"); }
        return reply("accepted");
    } catch (...) { return exception_reply(); }
}

/// enqueue(body): копирует допустимое UTF-8 тело и резервирует bytes/frames без ожидания mutex или сети.
NETRUN_EXPORT char* netrun_try_enqueue(unsigned int argc, char** argv) noexcept {
    try {
        std::string body;
        if (argc != 1 || !argument(argc, argv, 0, MaxFrame, body) || body.empty() || !valid_utf8(body)) return reply("invalid_arguments");
        Guard guard(manager().gate, false); if (!guard) return reply("would_block");
        auto* connection = manager().connection.get();
        const auto state = manager().state.load();
        if (!connection || connection->stop.load() || (state != State::Connecting && state != State::Connected)) return reply("closed");
        if (body.size() > connection->frame_limit) return reply("frame_size");
        Guard queues(connection->queues, false); if (!queues) return reply("would_block");
        if (connection->outbound_frames >= connection->frame_count_limit || body.size() > connection->queue_limit - connection->outbound_bytes)
            return reply("would_block");
        connection->outbound.push_back(std::move(body));
        connection->outbound_bytes += connection->outbound.back().size(); ++connection->outbound_frames;
        return reply("accepted");
    } catch (...) { return exception_reply(); }
}

/// poll(max_frames,max_bytes): ограничивает именно сериализованный ответ, не теряя слишком большой front.
NETRUN_EXPORT char* netrun_poll(unsigned int argc, char** argv) noexcept {
    try {
        std::string frames, bytes;
        std::size_t f, b;
        if (argc != 2 || !argument(argc, argv, 0, 3, frames) || !number(frames, 1, MaxFrames, f) ||
            !argument(argc, argv, 1, 7, bytes) || !number(bytes, 128, MaxPoll, b)) return poll_reply("invalid_arguments");
        Guard guard(manager().gate, false); if (!guard) return poll_reply("would_block");
        auto* connection = manager().connection.get();
        if (!connection) { result_buffer = "{\"frames\":[],\"state\":\"closed\",\"code\":\"closed\"}"; return result_buffer.data(); }
        Guard queues(connection->queues, false); if (!queues) return poll_reply("would_block");
        const std::string prefix = "{\"frames\":[";
        const std::string suffix = std::string("],\"state\":\"") + state_name(manager().state.load()) + "\",\"code\":\"";
        const char* worker_code = error_name(manager().error.load());
        const auto code_length = std::strlen(worker_code) > std::strlen("would_block") ? std::strlen(worker_code) : std::strlen("would_block");
        std::size_t count = 0, size = prefix.size() + suffix.size() + code_length + 2;
        for (const auto& frame : connection->inbound) {
            if (count >= f) break;
            const auto next = escaped_size(frame) + (count ? 1 : 0);
            if (next > b - size) break;
            size += next; ++count;
        }
        result_buffer = prefix;
        result_buffer.reserve(size);
        std::size_t consumed_bytes = 0;
        for (std::size_t i = 0; i < count; ++i) {
            if (i) result_buffer += ',';
            append_escaped(result_buffer, connection->inbound[i]);
            consumed_bytes += connection->inbound[i].size();
        }
        result_buffer += suffix;
        result_buffer += count == 0 && !connection->inbound.empty() ? "would_block" : worker_code;
        result_buffer += "\"}";
        // Dequeue происходит только после успешной сериализации целого ответа.
        for (std::size_t i = 0; i < count; ++i) connection->inbound.pop_front();
        connection->inbound_bytes -= consumed_bytes; connection->inbound_frames -= count;
        return result_buffer.data();
    } catch (...) { return exception_reply(); }
}

/// status(): снимок bounded accounting; секреты и payload не включаются.
NETRUN_EXPORT char* netrun_status(unsigned int argc, char**) noexcept {
    try {
        if (argc != 0) return reply("invalid_arguments");
        Guard guard(manager().gate, false); if (!guard) return reply("would_block");
        auto* connection = manager().connection.get();
        if (!connection) return reply("closed");
        if (connection->stop.load() && connection->exited.load(std::memory_order_acquire)) manager().state.store(State::Closed);
        Guard queues(connection->queues, false); if (!queues) return reply("would_block");
        reply(error_name(manager().error.load())); result_buffer.pop_back();
        result_buffer += ",\"outbound_frames\":" + std::to_string(connection->outbound_frames);
        result_buffer += ",\"outbound_bytes\":" + std::to_string(connection->outbound_bytes);
        result_buffer += ",\"inbound_frames\":" + std::to_string(connection->inbound_frames);
        result_buffer += ",\"inbound_bytes\":" + std::to_string(connection->inbound_bytes) + "}";
        return result_buffer.data();
    } catch (...) { return exception_reply(); }
}

/// close(): отмечает остановку; shutdown/close выполняет worker, никакого join на потоке BYOND.
NETRUN_EXPORT char* netrun_close(unsigned int argc, char**) noexcept {
    try {
        if (argc != 0) return reply("invalid_arguments");
        Guard guard(manager().gate, false); if (!guard) return reply("would_block");
        auto* connection = manager().connection.get();
        if (!connection || connection->exited.load(std::memory_order_acquire)) { manager().state.store(State::Closed); return reply("closed"); }
        manager().state.store(State::Closing); connection->stop.store(true);
        return reply("closing");
    } catch (...) { return exception_reply(); }
}
