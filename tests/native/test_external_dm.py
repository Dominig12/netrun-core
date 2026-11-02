"""Воспроизводимый Windows E2E: настоящий BYOND 516 → x86 bridge → локальный Host."""
from __future__ import annotations

import argparse
import os
from pathlib import Path

from tool_paths import find_dotnet
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from urllib.parse import urlencode


# Все build artifacts остаются ignored, а world и secret создаются вне обоих репозиториев.
ROOT = Path(__file__).resolve().parents[2]
HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0


def tool_path(explicit: str | None, name: str, fallback: Path) -> Path:
    """Выбирает установленный инструмент; runner не скачивает SDK и не меняет PATH."""
    candidate = Path(explicit) if explicit else Path(shutil.which(name) or fallback)
    if not candidate.is_file():
        raise RuntimeError(f"tool_missing: {name}; supply its explicit path")
    return candidate.resolve()


def checked_run(arguments: list[str], directory: Path, timeout: float) -> str:
    """Ограничивает время вспомогательной сборки и сохраняет проверяемую диагностику без секретов."""
    completed = subprocess.run(arguments, cwd=directory, capture_output=True, text=True,
                               encoding="utf-8", errors="replace", timeout=timeout,
                               creationflags=HIDDEN)
    output = completed.stdout + completed.stderr
    if completed.returncode:
        raise RuntimeError(f"process_failed: {Path(arguments[0]).name}\n{output}")
    return output


def unused_loopback_port() -> int:
    """Назначает ephemeral local port; занятый между bind и Host стартом порт даёт явный отказ."""
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def stop_process(process: subprocess.Popen | None) -> None:
    """Сначала просит процесс завершиться, затем ограничивает аварийную остановку пятью секундами."""
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def run(arguments: argparse.Namespace) -> None:
    """Компилирует только tiny world; реальный station world и production infrastructure не запускаются."""
    if os.name != "nt":
        raise RuntimeError("windows_byond_runner_required; Linux BYOND runtime is a separate check")
    source = Path(arguments.source).resolve()
    integration = source / "modular_bluemoon/code/modules/netrun/integration"
    included = [ROOT / "tests/native/external_harness.dm",
                integration / "external_core.dm", integration / "external_effects.dm"]
    library = Path(arguments.library).resolve()
    rust_g = source / "rust_g.dll"
    for required in [*included, library, rust_g]:
        if not required.is_file():
            raise RuntimeError(f"required_file_missing: {required}")
    dotnet = tool_path(str(find_dotnet(arguments.dotnet)), "dotnet", Path("dotnet.exe"))
    byond_bin = Path(arguments.byond_bin)
    compiler = tool_path(None, "dm", byond_bin / "dm.exe")
    daemon = tool_path(None, "dd", byond_bin / "dd.exe")
    if not arguments.no_build and not arguments.host_dll:
        checked_run([str(dotnet), "build", str(ROOT / "src/Netrun.Host/Netrun.Host.csproj"),
                     "--configuration", "Release", "--nologo"], ROOT, 180)
    host_dll = Path(arguments.host_dll).resolve() if arguments.host_dll else ROOT / "src/Netrun.Host/bin/Release/net10.0/Netrun.Host.dll"
    if not host_dll.is_file():
        raise RuntimeError("host_build_missing")
    port = arguments.port or unused_loopback_port()
    host = None
    world = None
    token = secrets.token_urlsafe(48)
    with tempfile.TemporaryDirectory(prefix="netrun-external-") as folder:
        temporary = Path(folder)
        token_file = temporary / "session-token.txt"
        token_file.write_text(token, encoding="ascii")
        wrapper = temporary / "external.dme"
        wrapper.write_text("".join(f'#include "{path.as_posix()}"\n' for path in included), encoding="utf-8")
        built = checked_run([str(compiler), str(wrapper)], temporary, 30)
        if "0 errors, 0 warnings" not in built:
            raise RuntimeError(f"tiny_world_compile_not_clean\n{built}")
        environment = os.environ.copy()
        environment.pop("NETRUN_SESSION_TOKEN", None)
        environment["NETRUN_SESSION_TOKEN_FILE"] = str(token_file)
        log_path = temporary / "host.log"
        with log_path.open("w", encoding="utf-8") as host_log:
            try:
                host = subprocess.Popen([str(dotnet), str(host_dll), "serve", str(port)], cwd=ROOT,
                                        env=environment, stdout=host_log, stderr=subprocess.STDOUT,
                                        creationflags=HIDDEN)
                deadline = time.monotonic() + 10
                while "NETRUN Host listening on loopback:" not in log_path.read_text(encoding="utf-8"):
                    if host.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError("host_start_failed")
                    time.sleep(0.05)
                parameters = urlencode({"library": library.as_posix(), "port": str(port),
                                        "token_file": token_file.as_posix(), "rust_g": rust_g.as_posix()})
                world = subprocess.Popen([str(daemon), str(wrapper.with_suffix(".dmb")), "-close", "-trusted",
                                          "-invisible", "-verbose", "-params", parameters], cwd=temporary,
                                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                         encoding="utf-8", errors="replace", creationflags=HIDDEN)
                output, _ = world.communicate(timeout=arguments.timeout)
                if world.returncode or "EXTERNAL_PASS snapshot_churn_and_ack machine_lifecycle" not in output or "EXTERNAL_FAIL" in output:
                    raise RuntimeError(f"external_lifecycle_failed\n{output.replace(token, '<redacted>')}")
                print("PASS: BYOND516 -> bridge -> Host snapshot/catchup/churn -> native message lifecycle -> safe output ACK")
            finally:
                stop_process(world)
                stop_process(host)


def main() -> int:
    """CLI задаёт только пути и port; сырой token никогда не попадает в argv или диагностику."""
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", default=str(ROOT.parent / "MOLOT-BlueMoon-Station"))
    parser.add_argument("--library", default=str(ROOT / "native/bin/netrun_bridge.dll"))
    parser.add_argument("--dotnet")
    parser.add_argument("--host-dll", help="use an explicitly reviewed published Host artifact")
    parser.add_argument("--byond-bin", default="C:/Program Files (x86)/BYOND/bin")
    parser.add_argument("--port", type=int)
    parser.add_argument("--timeout", type=float, default=45)
    parser.add_argument("--no-build", action="store_true", help="use the existing Release Host build")
    arguments = parser.parse_args()
    if arguments.port is not None and not 1 <= arguments.port <= 65535:
        parser.error("port must be in 1..65535")
    if arguments.timeout <= 0:
        parser.error("timeout must be positive")
    try:
        run(arguments)
        return 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
