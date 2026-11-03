"""Общий tiny-world wrapper: реальные exporter/effects и production telemetry receiver без admin UI."""
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


def prepare_external_wrapper(source: Path, directory: Path, harness: Path | None = None) -> Path:
    """Копирует receiver/sampler verbatim; явные границы не позволяют незаметно заменить их stub."""
    integration = source / "modular_bluemoon/code/modules/netrun/integration"
    monitor = source / "modular_bluemoon/code/modules/netrun/core/library_monitor.dm"
    production = monitor.read_text(encoding="utf-8")
    start_marker = "/datum/netrun_external_core\n"
    end_marker = "/// Составные строки тоже различают неизвестное значение и реальный ноль."
    if production.count(start_marker) != 1 or production.count(end_marker) != 1:
        raise RuntimeError("production_monitor_fixture_boundaries_changed")
    start = production.index(start_marker)
    end = production.index(end_marker, start)
    receiver = production[start:end]
    required = ("monitor_now", "monitor_age", "process_monitor_frame", "update_host_metrics", "sample_library_monitor")
    if any(receiver.count(f"/datum/netrun_external_core/proc/{name}(") != 1 for name in required):
        raise RuntimeError("production_monitor_fixture_receiver_changed")
    sampled = directory / "monitor_receiver.dm"
    sampled.write_text(receiver, encoding="utf-8")
    included = (harness or ROOT / "tests/native/external_harness.dm",
                integration / "external_core.dm", integration / "external_effects.dm", sampled)
    wrapper = directory / "external.dme"
    wrapper.write_text("".join(f'#include "{path.as_posix()}"\n' for path in included), encoding="utf-8")
    return wrapper
