"""Пути native-test toolchain совпадают со штатным portable bootstrap."""
import json
import os
from pathlib import Path
import shutil


def find_dotnet(explicit: str | None = None) -> Path:
    """Явный SDK, PATH либо штатный NETRUN cache; пользовательские Codex caches не нужны."""
    selected = explicit or os.environ.get("NETRUN_DOTNET") or shutil.which("dotnet")
    if selected:
        return Path(selected).resolve()
    root = Path(__file__).resolve().parents[2]
    version = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    cache = Path(os.environ["NETRUN_TOOLCHAIN_CACHE"]) if os.environ.get("NETRUN_TOOLCHAIN_CACHE") else (
        Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) / "NETRUN/toolchains")
    return cache / f"dotnet-{version}-windows-x64/dotnet.exe"
