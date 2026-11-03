"""Реальный Linux x64 ctypes/socket smoke; не заменяет прогон BYOND x86 на production платформе."""
from __future__ import annotations

import argparse
import ctypes
import json
from pathlib import Path
import socket
import struct
import subprocess
import sys
import threading
import time
import unittest


# Разрядность библиотеки должна совпадать с Python этого test runner.
LIBRARY = Path(__file__).resolve().parents[2] / "native/bin/libnetrun_bridge-x64.so"


def exact(client, size):
    """Получает конечный объём wire bytes и отличает EOF от частичного чтения."""
    data = bytearray()
    while len(data) < size:
        part = client.recv(size - len(data))
        if not part:
            raise AssertionError("unexpected socket EOF")
        data.extend(part)
    return bytes(data)


def frame(body):
    """Эталонный test peer пишет unsigned network-order длину независимо от native реализации."""
    return struct.pack("!I", len(body)) + body


class Server:
    """Один loopback peer с ограниченными socket/thread сроками."""

    def __init__(self, action):
        """Сразу запускает test peer на ephemeral port, не открывая внешний сетевой интерфейс."""
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.listener.settimeout(3)
        self.port = self.listener.getsockname()[1]
        self.error = None
        self.action = action
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()

    def run(self):
        """Сохраняет ошибку peer, чтобы исключение в thread не превратилось в ложный PASS."""
        try:
            with self.listener.accept()[0] as client:
                client.settimeout(3)
                self.action(client)
        except Exception as error:
            self.error = error
        finally:
            self.listener.close()

    def finish(self):
        """Дожидается именно завершения peer и проверяет его исход."""
        self.thread.join(4)
        if self.thread.is_alive():
            raise AssertionError("test peer did not stop")
        if self.error:
            raise self.error


class LinuxBridgeTests(unittest.TestCase):
    """Загружает настоящий ELF64 через ABI, без mock очередей или worker."""

    @classmethod
    def setUpClass(cls):
        """Фиксирует exact legacy argc/argv ABI всех пяти публичных exports."""
        cls.library = ctypes.CDLL(str(LIBRARY.resolve()))
        for name in ("open", "try_enqueue", "poll", "status", "close"):
            function = getattr(cls.library, "netrun_" + name)
            function.argtypes = [ctypes.c_uint, ctypes.POINTER(ctypes.c_char_p)]
            function.restype = ctypes.c_char_p

    def call(self, operation, *arguments):
        """Копирует C string ответа и разбирает ограниченную ABI-оболочку."""
        values = [value.encode() if isinstance(value, str) else value for value in arguments]
        argv = (ctypes.c_char_p * len(values))(*values)
        raw = getattr(self.library, "netrun_" + operation)(len(values), argv)
        self.assertIsNotNone(raw)
        return json.loads(raw)

    def wait(self, predicate):
        """Polling выполняется только в тесте; ABI не должен ждать socket I/O."""
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            value = predicate()
            if value:
                return value
            time.sleep(0.005)
        self.fail("native bridge timeout")

    def open(self, server):
        """Открывает один ограниченный worker и ждёт перехода состояния вне ABI вызова."""
        self.assertEqual("accepted", self.call("open", "127.0.0.1", str(server.port), "8", "1048576", "1048576")["code"])
        self.wait(lambda: self.call("status")["state"] in ("connected", "disconnected"))

    def tearDown(self):
        """Следующий тест не переиспользует singleton до подтверждённого завершения прежнего worker."""
        self.call("close")
        self.wait(lambda: self.call("status")["state"] == "closed")

    def test_exports_are_exact(self):
        """Проверяет dynamic export table и фактическую загрузку того же ELF64 в setUpClass."""
        output = subprocess.check_output(["nm", "-D", "--defined-only", str(LIBRARY)], text=True, timeout=30)
        exported = {line.split()[-1] for line in output.splitlines() if line.split()}
        self.assertEqual({"netrun_open", "netrun_try_enqueue", "netrun_poll", "netrun_status", "netrun_close"}, exported)

    def test_fragmented_and_concatenated_frames(self):
        """Header/body по одному байту и соседний кадр сохраняют exact UTF-8 и JSON-форму."""
        body = '{"text":"А","nested":[null,{},[]]}'.encode()
        def action(client):
            size = struct.unpack("!I", exact(client, 4))[0]
            self.assertEqual(body, exact(client, size))
            for byte in frame(body) + frame(b'{"second":true}'):
                client.sendall(bytes([byte]))
        server = Server(action)
        self.open(server)
        self.assertEqual("accepted", self.call("try_enqueue", body)["code"])
        received = []
        def poll():
            received.extend(self.call("poll", "8", "1048576").get("frames", []))
            return len(received) == 2
        self.wait(poll)
        self.assertEqual([body.decode(), '{"second":true}'], received)
        server.finish()

    def test_oversized_prefix_is_rejected_without_body(self):
        """Недопустимая длина закрывает вход до ожидания или выделения тела."""
        server = Server(lambda client: client.sendall(struct.pack("!I", 1048577)))
        self.open(server)
        status = self.wait(lambda: (value if (value := self.call("status"))["state"] == "disconnected" else None))
        self.assertEqual("frame_size", status["code"])
        self.assertEqual(0, status["inbound_bytes"])
        server.finish()

    def test_close_is_bounded_and_revokes_admission(self):
        """Close возвращает немедленно, пока peer независимо наблюдает завершение socket."""
        def action(client):
            while client.recv(1024):
                pass
        server = Server(action)
        self.open(server)
        started = time.monotonic()
        self.call("close")
        self.assertLess(time.monotonic() - started, 0.2)
        self.assertEqual("closed", self.call("try_enqueue", "{}")["code"])
        server.finish()

    def test_active_worker_survives_owner_dlclose(self):
        """Отдельный процесс отзывает caller handle при живом worker и проверяет resident ELF до close."""
        def action(client):
            try:
                while client.recv(1024):
                    pass
            except ConnectionResetError:
                # Возможный baseline SIGSEGV должен оставаться основной ошибкой subprocess, не socket cleanup.
                pass
        server = Server(action)
        script = r'''
import _ctypes, ctypes, json, sys, time
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
_ctypes.dlclose(library._handle)
time.sleep(0.1)
assert call("status")["state"] == "connected", "active worker lost its module or connection"
call("close")
deadline = time.monotonic() + 3
while call("status")["state"] != "closed":
    assert time.monotonic() < deadline, "worker did not close"
    time.sleep(0.005)
print("NATIVE_PASS resident_elf", flush=True)
'''
        try:
            ran = subprocess.run([sys.executable, "-c", script, str(LIBRARY.resolve()), str(server.port)],
                                 capture_output=True, text=True, timeout=10)
            self.assertEqual(0, ran.returncode, ran.stdout + ran.stderr)
            self.assertIn("NATIVE_PASS resident_elf", ran.stdout)
        finally:
            server.finish()


if __name__ == "__main__":
    # Путь библиотеки задаётся явно CI; аргументы unittest сохраняются отдельно.
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--library", type=Path, default=LIBRARY)
    parsed, remaining = parser.parse_known_args()
    LIBRARY = parsed.library
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
