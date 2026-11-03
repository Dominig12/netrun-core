"""Socket/ABI проверки без станции; x64 ctypes и отдельный настоящий x86 BYOND harness."""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path

from tool_paths import find_dotnet
import secrets
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest


ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT / "native/bin/netrun_bridge-x64.dll"
HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0


class Server:
    """Одно loopback подключение; поведение server задаёт тест, а не мост."""

    def __init__(self, action, receive_buffer=None):
        self.listener = socket.socket()
        if receive_buffer:
            self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, receive_buffer)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.listener.settimeout(3)
        self.port = self.listener.getsockname()[1]
        self.error = None
        self.action = action
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        """Не допускает незаметно успешного теста при исключении socket-server."""
        try:
            with self.listener.accept()[0] as client:
                client.settimeout(3)
                self.action(client)
        except (OSError, AssertionError) as error:
            self.error = error
        finally:
            self.listener.close()

    def finish(self):
        """Сервер также имеет ограниченное время остановки."""
        self.thread.join(4)
        if self.thread.is_alive():
            raise AssertionError("test server did not stop")
        if self.error:
            raise self.error


def exact(client, size):
    """Читает заданное число байтов, различая обрыв и частичный recv."""
    result = bytearray()
    while len(result) < size:
        fragment = client.recv(size - len(result))
        if not fragment:
            raise AssertionError("unexpected socket EOF")
        result.extend(fragment)
    return bytes(result)


def read_frame(client):
    """Эталонный test peer использует unsigned network-order длину."""
    return exact(client, struct.unpack("!I", exact(client, 4))[0])


def wire(body):
    """Собирает один эталонный length-prefixed кадр."""
    return struct.pack("!I", len(body)) + body


class BridgeTests(unittest.TestCase):
    """Вызовы идут через реальные exports библиотеки, без mock внутренней очереди."""

    @classmethod
    def setUpClass(cls):
        """Для ctypes нужна разрядность Python; BYOND x86 проверяется отдельно ниже."""
        cls.library = ctypes.CDLL(str(LIBRARY))
        for name in ("open", "try_enqueue", "poll", "status", "close"):
            function = getattr(cls.library, "netrun_" + name)
            function.argtypes = [ctypes.c_uint, ctypes.POINTER(ctypes.c_char_p)]
            function.restype = ctypes.c_char_p

    def call(self, name, *arguments):
        """Копирует returned C string так же, как legacy call_ext копирует её для DM."""
        values = [value.encode() if isinstance(value, str) else value for value in arguments]
        argv = (ctypes.c_char_p * len(values))(*values)
        raw = getattr(self.library, "netrun_" + name)(len(values), argv)
        self.assertIsNotNone(raw)
        return json.loads(raw)

    def wait(self, predicate, timeout=3):
        """Ограниченный polling только теста; в C ABI нет ожидания сети."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = predicate()
            if value:
                return value
            time.sleep(0.005)
        self.fail("timed out waiting for native bridge")

    def open(self, server, frames=8, frame_bytes=1048576, queue_bytes=1048576):
        """Открывает единственный worker и дожидается соединения вне вызова ABI."""
        self.assertEqual("accepted", self.call("open", "127.0.0.1", str(server.port), str(frames), str(queue_bytes), str(frame_bytes))["code"])
        self.wait(lambda: self.call("status")["state"] in ("connected", "disconnected"))

    def tearDown(self):
        """Новый тест не начинает соединение, пока прежний worker не подтвердил остановку."""
        self.call("close")
        self.wait(lambda: self.call("status")["state"] == "closed")

    def test_fragmented_and_concatenated_frames_roundtrip(self):
        """Кадры переживают дробление header/body и несколько сообщений в одном send."""
        body = '{"text":"А\\\"","nested":[null,{},[]]}'.encode()
        def action(client):
            self.assertEqual(body, read_frame(client))
            response = wire(body) + wire(b'{"second":true}')
            for byte in response:
                client.sendall(bytes([byte]))
        server = Server(action)
        self.open(server)
        self.assertEqual("accepted", self.call("try_enqueue", body)["code"])
        results = []
        def poll():
            results.extend(self.call("poll", "8", "1048576").get("frames", []))
            return len(results) == 2
        self.wait(poll)
        self.assertEqual([body.decode(), '{"second":true}'], results)
        server.finish()

    def test_poll_budget_keeps_front_and_accounts_escaping(self):
        """Маленький бюджет не удаляет front; учитываются bytes реального JSON-ответа."""
        body = (b'"\\\x01' * 200)
        server = Server(lambda client: client.sendall(wire(body)))
        self.open(server)
        self.wait(lambda: self.call("status").get("inbound_frames") == 1)
        small = self.call("poll", "8", "128")
        self.assertEqual([], small["frames"])
        self.assertEqual("would_block", small["code"])
        self.assertEqual(1, self.call("status")["inbound_frames"])
        full = self.call("poll", "8", "8192")
        self.assertEqual([body.decode()], full["frames"])
        self.assertLessEqual(len(json.dumps(full, ensure_ascii=False, separators=(",", ":")).encode()), 8192)
        self.assertEqual(0, self.call("status")["inbound_frames"])
        server.finish()

    def test_oversized_header_disconnects_before_body(self):
        """Достаточно prefix: тело недопустимого размера мост не ждёт и не выделяет."""
        server = Server(lambda client: client.sendall(struct.pack("!I", 1048577)))
        self.open(server)
        result = self.wait(lambda: (status if (status := self.call("status"))["state"] == "disconnected" else None))
        self.assertEqual("frame_size", result["code"])
        self.assertEqual(0, result["inbound_bytes"])
        server.finish()

    def test_invalid_utf8_disconnects_without_publishing_frame(self):
        """Невалидные network bytes не заменяются U+FFFD и не возвращаются DM как другой payload."""
        server = Server(lambda client: client.sendall(wire(b'\xed\xa0\x80')))
        self.open(server)
        result = self.wait(lambda: (status if (status := self.call("status"))["state"] == "disconnected" else None))
        self.assertEqual("invalid_utf8", result["code"])
        self.assertEqual([], self.call("poll", "8", "1024")["frames"])
        server.finish()

    def test_inbound_queue_is_bounded_including_inflight(self):
        """При заполнении очереди worker сохраняет header и применяет TCP backpressure."""
        bodies = [bytes([65 + i]) * 64 for i in range(5)]
        server = Server(lambda client: client.sendall(b"".join(wire(body) for body in bodies)))
        self.open(server, frames=2)
        self.wait(lambda: self.call("status").get("inbound_frames") == 2)
        time.sleep(0.1)
        self.assertLessEqual(self.call("status")["inbound_frames"], 2)
        results = []
        def poll():
            results.extend(self.call("poll", "1", "1024").get("frames", []))
            self.assertLessEqual(self.call("status").get("inbound_frames", 0), 2)
            return len(results) == len(bodies)
        self.wait(poll)
        self.assertEqual([body.decode() for body in bodies], results)
        server.finish()

    def test_invalid_arguments_and_closed_enqueue_are_safe(self):
        """ABI не делает DNS и не принимает сломанные limits, UTF-8 или вызовы без аргументов."""
        self.assertEqual("invalid_arguments", self.call("open", "example.com", "1", "8", "1048576", "1048576")["code"])
        self.assertEqual("invalid_arguments", self.call("open", "127.0.0.1", "01", "8", "1048576", "1048576")["code"])
        for invalid in ("00", "01", "+0", "-0", "18446744073709551616"):
            with self.subTest(invalid=invalid):
                self.assertEqual("invalid_arguments", self.call("open", "127.0.0.1", "1", invalid, "0", "1048576")["code"])
                self.assertEqual("invalid_arguments", self.call("open", "127.0.0.1", "1", "0", invalid, "1048576")["code"])
        self.assertEqual("invalid_arguments", self.call("open", "127.0.0.1", "0", "0", "0", "1048576")["code"])
        self.assertEqual("invalid_arguments", self.call("open", "127.0.0.1", "1", "0", "0", "0")["code"])
        self.assertEqual("invalid_arguments", self.call("poll", "0", "1048576")["code"])
        self.assertEqual("invalid_arguments", self.call("try_enqueue", b'\xff')["code"])
        self.assertEqual("invalid_arguments", self.call("try_enqueue")["code"])
        self.assertEqual("closed", self.call("try_enqueue", "{}")["code"])

    def test_zero_quotas_accept_burst_beyond_finite_queue_defaults(self):
        """Production open(0,0) не ждёт poll после 128 кадров или 1 MiB; framing ABI остаётся конечным."""
        bodies = [f"{index:04d}".encode() + b"x" * 8188 for index in range(160)]
        def action(client):
            self.assertEqual(b"zero-quotas", read_frame(client))
            client.sendall(b"".join(wire(body) for body in bodies))
        server = Server(action)
        self.open(server, frames=0, queue_bytes=0)
        self.assertEqual("accepted", self.call("try_enqueue", "zero-quotas")["code"])
        status = self.wait(lambda: (value if (value := self.call("status")).get("inbound_frames") == len(bodies) else None))
        self.assertEqual(sum(map(len, bodies)), status["inbound_bytes"])
        received = []
        def poll():
            received.extend(self.call("poll", "128", "8388608").get("frames", []))
            return len(received) == len(bodies)
        self.wait(poll)
        self.assertEqual([body.decode() for body in bodies], received)
        self.assertEqual(0, self.call("status")["inbound_bytes"])
        server.finish()

    def test_zero_queue_quota_can_be_combined_with_finite_other_quota(self):
        """Count и byte quotas независимо принимают canonical zero, конечная соседняя настройка сохраняется."""
        for frames, queue_bytes in ((0, 1048576), (8, 0)):
            with self.subTest(frames=frames, queue_bytes=queue_bytes):
                server = Server(lambda client: client.sendall(wire(read_frame(client))))
                self.open(server, frames=frames, queue_bytes=queue_bytes)
                self.assertEqual("accepted", self.call("try_enqueue", "mixed-quotas")["code"])
                received = []
                def poll():
                    received.extend(self.call("poll", "8", "1048576").get("frames", []))
                    return bool(received)
                self.wait(poll)
                self.assertEqual(["mixed-quotas"], received)
                self.call("close")
                self.wait(lambda: self.call("status")["state"] == "closed")
                server.finish()

    def test_close_is_bounded_and_prevents_new_admission(self):
        """Close не ждёт peer и не позволяет создать второй worker поверх закрывающегося."""
        closed = threading.Event()
        def action(client):
            while client.recv(1024):
                pass
            closed.set()
        server = Server(action)
        self.open(server)
        self.assertEqual("already_open", self.call("open", "127.0.0.1", str(server.port), "8", "1048576", "1048576")["code"])
        before = time.monotonic()
        self.call("close")
        self.assertLess(time.monotonic() - before, 0.1)
        self.assertEqual("closed", self.call("try_enqueue", "{}")["code"])
        self.wait(closed.is_set)
        server.finish()

    def test_outbound_quota_includes_partially_written_frame(self):
        """Peer не читает тело: reservation нельзя освободить при переносе кадра из queue в worker."""
        release = threading.Event()
        def action(client):
            if not release.wait(3):
                raise AssertionError("test did not release peer")
            while client.recv(8192):
                pass
        server = Server(action, receive_buffer=4096)
        self.open(server, frames=1)
        body = b'x' * 1048576
        self.assertEqual("accepted", self.call("try_enqueue", body)["code"])
        self.assertEqual("would_block", self.call("try_enqueue", body)["code"])
        status = self.call("status")
        self.assertEqual(1, status["outbound_frames"])
        self.assertEqual(1048576, status["outbound_bytes"])
        self.call("close")
        release.set()
        server.finish()

    def test_duplex_burst_preserves_order_and_releases_body_quotas(self):
        """A full duplex burst crosses queue limits and partial reads without losing frame ownership."""
        bodies = [json.dumps({"index": index, "body": "x" * 32768}).encode() for index in range(24)]
        def action(client):
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            for body in bodies:
                self.assertEqual(body, read_frame(client))
                client.sendall(wire(body))
        server = Server(action)
        self.open(server, frames=8)
        sent = 0
        received = []
        deadline = time.monotonic() + 5
        while len(received) < len(bodies) and time.monotonic() < deadline:
            if sent < len(bodies):
                code = self.call("try_enqueue", bodies[sent])["code"]
                self.assertIn(code, ("accepted", "would_block"))
                sent += code == "accepted"
            received.extend(self.call("poll", "2", "1048576").get("frames", []))
            time.sleep(0.001)
        self.assertEqual([body.decode() for body in bodies], received)
        status = self.wait(lambda: (value if (value := self.call("status")).get("outbound_frames") == 0 else None))
        self.assertEqual(0, status["outbound_bytes"])
        self.assertEqual(0, status["inbound_frames"])
        self.assertEqual(0, status["inbound_bytes"])
        server.finish()

    def test_x86_and_x64_exports_match_declared_legacy_abi(self):
        """Читает PE без загрузки x86 кода в Python x64 и проверяет точные undecorated имена exports."""
        expected = {"netrun_open", "netrun_try_enqueue", "netrun_poll", "netrun_status", "netrun_close"}
        for path, machine_expected in ((LIBRARY, 0x8664), (ROOT / "native/bin/netrun_bridge.dll", 0x14C)):
            raw = path.read_bytes()
            pe = struct.unpack_from("<I", raw, 0x3C)[0]
            machine, section_count = struct.unpack_from("<HH", raw, pe + 4)
            self.assertEqual(machine_expected, machine)
            optional_size = struct.unpack_from("<H", raw, pe + 20)[0]
            optional = pe + 24
            magic = struct.unpack_from("<H", raw, optional)[0]
            export_rva = struct.unpack_from("<I", raw, optional + (96 if magic == 0x10B else 112))[0]
            sections = [struct.unpack_from("<IIII", raw, optional + optional_size + 40 * i + 8) for i in range(section_count)]
            def offset(rva):
                for virtual_size, virtual_address, raw_size, raw_offset in sections:
                    if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                        return raw_offset + rva - virtual_address
                raise AssertionError("export RVA outside file")
            exports = offset(export_rva)
            count = struct.unpack_from("<I", raw, exports + 24)[0]
            names = offset(struct.unpack_from("<I", raw, exports + 32)[0])
            actual = set()
            for i in range(count):
                start = offset(struct.unpack_from("<I", raw, names + 4 * i)[0])
                actual.add(raw[start:raw.index(b'\0', start)].decode("ascii"))
            self.assertEqual(expected, actual)

    def test_real_byond516_loads_x86_exports(self):
        """Минимальный отдельный DM-мир реально вызывает 32-bit DLL и проверяет framing через echo peer."""
        compiler = Path(r"C:/Program Files (x86)/BYOND/bin/dm.exe")
        daemon = compiler.with_name("dd.exe")
        if not compiler.exists() or not daemon.exists():
            self.skipTest("BYOND516 compiler/runtime unavailable")
        def action(client):
            client.sendall(wire(read_frame(client)))
        server = Server(action)
        with tempfile.TemporaryDirectory(prefix="netrun-native-") as folder:
            directory = Path(folder)
            (directory / "harness.dm").write_bytes((ROOT / "tests/native/bridge_harness.dm").read_bytes())
            (directory / "harness.dme").write_text('#include "harness.dm"\n', encoding="utf-8")
            built = subprocess.run([str(compiler), "harness.dme"], cwd=directory, capture_output=True, text=True, timeout=30)
            self.assertEqual(0, built.returncode, built.stdout + built.stderr)
            parameters = "library=" + str(ROOT / "native/bin/netrun_bridge.dll").replace("\\", "/") + "&port=" + str(server.port)
            try:
                ran = subprocess.run([str(daemon), "harness.dmb", "-close", "-trusted", "-invisible", "-verbose", "-params", parameters],
                                     cwd=directory, capture_output=True, text=True, timeout=15)
            except subprocess.TimeoutExpired as error:
                self.fail("isolated BYOND harness timeout: " + repr(error.stdout) + " / " + repr(error.stderr))
            self.assertEqual(0, ran.returncode, ran.stdout + ran.stderr)
            self.assertIn("NATIVE_PASS BYOND516", ran.stdout + ran.stderr)
        server.finish()

    def test_active_worker_keeps_windows_module_loaded_after_owner_free_library(self):
        """Выгрузка caller reference не может отнять исполняемый DLL-код у detached socket worker."""
        if os.name != "nt":
            self.skipTest("Windows loader lifetime regression")
        def action(client):
            try:
                while client.recv(1024):
                    pass
            except ConnectionResetError:
                # Baseline AV должен остаться основной ошибкой, а не потеряться за socket cleanup test peer.
                pass
        server = Server(action)
        # Отдельный процесс ограничивает возможный AV регрессионного кода, не повреждая Python test runner.
        script = r'''
import ctypes, json, sys, time
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.SetErrorMode(0x0001 | 0x0002)
kernel.FreeLibrary.argtypes = [ctypes.c_void_p]
kernel.FreeLibrary.restype = ctypes.c_int
kernel.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
kernel.GetModuleHandleW.restype = ctypes.c_void_p
library = ctypes.CDLL(sys.argv[1])
for name in ("open", "status", "close"):
    function = getattr(library, "netrun_" + name)
    function.argtypes = [ctypes.c_uint, ctypes.POINTER(ctypes.c_char_p)]
    function.restype = ctypes.c_char_p
def call(name, *arguments):
    values = [value.encode() for value in arguments]
    argv = (ctypes.c_char_p * len(values))(*values)
    return json.loads(getattr(library, "netrun_" + name)(len(values), argv))
assert call("open", "127.0.0.1", sys.argv[2], "8", "1048576", "1048576")["code"] == "accepted"
deadline = time.monotonic() + 3
while call("status")["state"] != "connected":
    assert time.monotonic() < deadline, "worker did not connect"
    time.sleep(0.005)
assert kernel.FreeLibrary(library._handle), "caller reference was not released"
time.sleep(0.1)
assert kernel.GetModuleHandleW(sys.argv[1]), "active worker module was unloaded"
call("close")
deadline = time.monotonic() + 3
while call("status")["state"] != "closed":
    assert time.monotonic() < deadline, "worker did not close"
    time.sleep(0.005)
print("NATIVE_PASS module_lifetime", flush=True)
'''
        try:
            ran = subprocess.run([sys.executable, "-c", script, str(LIBRARY), str(server.port)],
                                 cwd=ROOT, capture_output=True, text=True, timeout=10, creationflags=HIDDEN)
            self.assertEqual(0, ran.returncode, ran.stdout + ran.stderr)
            self.assertIn("NATIVE_PASS module_lifetime", ran.stdout)
        finally:
            server.finish()

    def test_real_byond516_shutdown_with_active_production_client_worker(self):
        """Production exporter/effects могут завершить tiny world при живом worker без доступа к выгруженной DLL."""
        if os.name != "nt":
            self.skipTest("Windows BYOND shutdown regression")
        compiler = Path(r"C:/Program Files (x86)/BYOND/bin/dm.exe")
        daemon = compiler.with_name("dd.exe")
        source = ROOT.parent / "MOLOT-BlueMoon-Station"
        integration = source / "modular_bluemoon/code/modules/netrun/integration"
        dotnet = find_dotnet()
        pointer = source / "data/netrun-core/build.json"
        if not all(path.is_file() for path in (compiler, daemon, dotnet, pointer,
                integration / "external_core.dm", integration / "external_effects.dm", source / "rust_g.dll")):
            self.skipTest("reviewed local Host/BYOND/station fixture unavailable")
        host_dll = Path(json.loads(pointer.read_text(encoding="utf-8"))["directory"]) / "host/Netrun.Host.dll"
        if not host_dll.is_file():
            self.skipTest("published Host bundle unavailable")
        with tempfile.TemporaryDirectory(prefix="netrun-active-shutdown-") as folder:
            directory = Path(folder)
            token = secrets.token_urlsafe(48)
            token_file = directory / "token.txt"
            token_file.write_text(token, encoding="ascii")
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            harness = (ROOT / "tests/native/external_harness.dm").read_text(encoding="utf-8")
            # Existing unsafe-ACK test намеренно abort-ит client; здесь нужно завершение здорового live worker.
            unsafe_ack = harness.index("\t// Неизвестный effect")
            success = harness.index('\tworld.log << "EXTERNAL_PASS snapshot_churn_and_ack machine_lifecycle"', unsafe_ack)
            harness = harness[:unsafe_ack] + harness[success:]
            closing = "\tclient.close()\n\tsleep(2)\n\tshutdown()"
            self.assertEqual(1, harness.count(closing), "fixture must have one explicit success cleanup")
            harness = harness.replace(closing, '''\tvar/list/worker = client.native_call("netrun_status", list())
\tif(worker?["state"] != "connected")
\t\tworld.log << "EXTERNAL_FAIL worker_not_active"
\t\tshutdown()
\t\treturn
\tworld.log << "NATIVE_PASS active_worker_shutdown"
\tshutdown()''')
            fixture = directory / "harness.dm"
            fixture.write_text(harness, encoding="utf-8")
            wrapper = directory / "harness.dme"
            wrapper.write_text("".join(f'#include "{path.as_posix()}"\n' for path in
                (fixture, integration / "external_core.dm", integration / "external_effects.dm")), encoding="utf-8")
            built = subprocess.run([str(compiler), str(wrapper)], cwd=directory, capture_output=True,
                                   text=True, timeout=30, creationflags=HIDDEN)
            self.assertEqual(0, built.returncode, built.stdout + built.stderr)
            self.assertIn("0 errors, 0 warnings", built.stdout + built.stderr)
            environment = os.environ.copy()
            environment.pop("NETRUN_SESSION_TOKEN", None)
            environment["NETRUN_SESSION_TOKEN_FILE"] = str(token_file)
            with (directory / "host.log").open("w", encoding="utf-8") as host_log:
                host = subprocess.Popen([str(dotnet), str(host_dll), "serve", str(port)], cwd=ROOT,
                                        env=environment, stdout=host_log, stderr=subprocess.STDOUT, creationflags=HIDDEN)
                try:
                    deadline = time.monotonic() + 10
                    while "NETRUN Host listening on loopback:" not in (directory / "host.log").read_text(encoding="utf-8"):
                        self.assertIsNone(host.poll(), "published Host exited before readiness")
                        self.assertLess(time.monotonic(), deadline, "published Host readiness timeout")
                        time.sleep(0.025)
                    parameters = "&".join(("library=" + str(ROOT / "native/bin/netrun_bridge.dll").replace("\\", "/"),
                        "port=" + str(port), "token_file=" + token_file.as_posix(), "rust_g=" + (source / "rust_g.dll").as_posix()))
                    ran = subprocess.run([str(daemon), str(wrapper.with_suffix(".dmb")), "-close", "-trusted", "-invisible", "-verbose", "-params", parameters],
                                         cwd=directory, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, creationflags=HIDDEN)
                    output = (ran.stdout + ran.stderr).replace(token, "<redacted>")
                    self.assertIn("NATIVE_PASS active_worker_shutdown", output)
                    self.assertNotIn("EXTERNAL_FAIL", output)
                    self.assertEqual(0, ran.returncode, output)
                    self.assertIsNone(host.poll(), "normal native EOF must not stop the multi-session Host")
                finally:
                    if host.poll() is None:
                        host.terminate()
                    try:
                        host.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        host.kill()
                        host.wait(timeout=5)

    def test_linux_x86_export_table_is_limited_without_executing_elf(self):
        """ELF32 i386 собран локально; чтение dynsym не притворяется Linux runtime-тестом."""
        raw = (ROOT / "native/bin/libnetrun_bridge.so").read_bytes()
        self.assertEqual(b"\x7fELF", raw[:4])
        self.assertEqual(1, raw[4])
        self.assertEqual(3, struct.unpack_from("<H", raw, 18)[0])
        start = struct.unpack_from("<I", raw, 32)[0]
        size, count = struct.unpack_from("<HH", raw, 46)
        sections = [struct.unpack_from("<IIIIIIIIII", raw, start + size * i) for i in range(count)]
        exports = set()
        for section in sections:
            if section[1] != 11:
                continue
            strings = sections[section[6]]
            for position in range(section[4], section[4] + section[5], section[9]):
                name, _, _, info, visibility, index = struct.unpack_from("<IIIBBH", raw, position)
                if index and info >> 4 in (1, 2) and info & 15 == 2 and visibility & 3 == 0:
                    offset = strings[4] + name
                    exports.add(raw[offset:raw.index(b'\0', offset)].decode("ascii"))
        self.assertEqual({"netrun_open", "netrun_try_enqueue", "netrun_poll", "netrun_status", "netrun_close"}, exports)


if __name__ == "__main__":
    unittest.main(verbosity=2)
