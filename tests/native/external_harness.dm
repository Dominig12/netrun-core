// Изолированные stubs для компиляции настоящего exporter: игровой checkout не запускается и не меняется.
// Wrapper .dme включает этот файл, затем integration/external_core.dm из исходного checkout.
#define TRUE 1
#define FALSE 0
#define QDELETED(value) (!(value))
#define SIGNAL_HANDLER
#define PROC_REF(value) "fixture_callback"
#define WEAKREF(value) new /datum/weakref(value)
#define COMSIG_NETRUN_NODE_REGISTERED "node_registered"
#define COMSIG_NETRUN_NODE_UNREGISTERED "node_unregistered"
#define COMSIG_NETRUN_NODE_MOVED "node_moved"
#define COMSIG_NETRUN_NODE_CHANGED "node_changed"
#define COMSIG_PARENT_QDELETING "qdeleting"
#define RUSTG_HASH_SHA256 "sha256"
#define NETRUN_NODE_APC "apc"
#define NETRUN_DEFAULT_FIRE_INTERVAL 20
#define rustg_generate_uuid_v4(...) call_ext(world.params["rust_g"], "uuid_v4")()
#define rustg_hash_string(algorithm, value) call_ext(world.params["rust_g"], "hash_string")(algorithm, value)
#define is_station_level(z) TRUE
#define is_mining_level(z) FALSE
#define UNIX "UNIX"
#define QDEL_NULL(value) if(value) { qdel(value); value = null; }
#define IS_FINITE(value) (isnum(value) && (value) == (value) && abs(value) <= 3.402823466e38)
// Tiny world не имеет MC; frame boundaries всегда уступают CPU, не меняя production pumps.
#define CHECK_TICK sleep(0)
#define SECONDS * 10
#define TICK_USAGE_REAL world.tick_usage
#define TICK_DELTA_TO_MS(percent_of_tick_used) ((percent_of_tick_used) * world.tick_lag)

/// Tiny harness не подменяет production qdel; только освобождает собственные fixture datums.
/proc/qdel(datum/value)
	if(value)
		value.Destroy()
		del(value)
/proc/trim(value)
	return value

/// Shutdown integration вызывает базовый SS hook; tiny world не имеет настоящего MC.
/datum/controller/subsystem/proc/Shutdown()
	return

/// Production shutdown diagnostics направляются только в test world log.
/proc/log_world(message)
	world.log << message

/// Stubs не эмулируют production signal bus; harness вызывает нужные callbacks явно.
/datum/proc/RegisterSignal(datum/source, signal, callback)
	return
/datum/proc/UnregisterSignal(datum/source, signal)
	return
/datum/proc/Destroy()
	return

/// Только test-lifetime reference; production exporter использует настоящий WEAKREF.
/datum/weakref
	var/datum/value
/datum/weakref/New(datum/target)
	value = target
/datum/weakref/proc/resolve()
	return value

/area
	var/area/base_area
/obj/machinery/power/apc
	var/area/area
/obj/machinery/ntnet_relay/proc/is_operational()
	return TRUE

/// Metadata compile stubs не создают components и не подменяют native graph.
/atom/proc/GetComponent(component_type)
	return null
/obj/machinery/telecomms
	var/list/freq_listening = list()
	var/list/links = list()
	var/on = TRUE
	var/toggled = TRUE
	var/long_range_link = FALSE
/obj/machinery/telecomms/proc/is_operational()
	return TRUE
/obj/machinery/telecomms/hub
/obj/machinery/telecomms/relay
	var/receiving = TRUE
	var/broadcasting = TRUE

/// Минимальные физические helpers сохраняют source geometry, а не формируют готовый граф.
/proc/get_turf(atom/value)
	while(value && !isturf(value))
		value = value.loc
	return value
/proc/get_area(atom/value)
	while(value && !isarea(value))
		value = value.loc
	return value
/proc/get_base_area(atom/value)
	var/area/room = get_area(value)
	return room?.base_area || room
/proc/netrun_sector_for(turf/position)
	return position ? "sector:1" : null
/proc/netrun_department_key(area/room)
	return "general"

/datum/component/netrun_node
	var/atom/parent
	var/address
	var/node_type = "apc"
	var/datum/component/netrun_node/bound_apc
/datum/component/netrun_node/proc/physical_turf()
	return get_turf(parent)
/datum/component/netrun_node/proc/is_online()
	return TRUE

/datum/netrun_machine_service
	var/service_id = "fixture.message"
	var/atom/host
	var/datum/component/netrun_node/node
	var/native_calls = 0
/datum/netrun_machine_service/proc/resolve_node()
	return node
/datum/netrun_machine_service/proc/service_tag()
	return node?.address
/datum/netrun_machine_service/proc/matches(datum/netrun_machine_service/source, tag)
	return isnull(tag) || tag == service_tag()
/datum/netrun_machine_service/proc/accepts(datum/netrun_machine_message/message)
	return node?.is_online() && message.source?.node?.is_online()
/datum/netrun_machine_service/proc/receive(datum/netrun_machine_message/message)
	native_calls++
	return TRUE

/// Original message остаётся в DM; Host получает только opaque context ID и JSON-копию payload.
/datum/netrun_machine_message
	var/list/payload
	var/list/outcomes
	var/datum/netrun_machine_service/source
	var/datum/netrun_machine_service/recipient
	var/datum/weakref/user_ref
	var/correlation_id
/datum/netrun_machine_message/New(list/data, list/counters)
	payload = json_decode(json_encode(data))
	outcomes = counters
/datum/netrun_machine_message/proc/delivered(datum/netrun_machine_service/service)
	outcomes["delivered"] = (outcomes["delivered"] || 0) + 1
/datum/netrun_machine_message/proc/failed(reason)
	outcomes["failed"] = (outcomes["failed"] || 0) + 1
/datum/netrun_machine_message/proc/coalescing_key()
	return null
/datum/netrun_machine_message/proc/transport_operation()
	return "message"
/datum/netrun_machine_message/proc/transport_revision()
	return null
/datum/netrun_machine_message/proc/can_replace(datum/netrun_machine_message/previous)
	return TRUE
/datum/netrun_machine_message/telecomms
	var/datum/signal/signal
/datum/signal
	var/frequency
/datum/netrun_machine_message/communications

/// Tiny protocol world не имеет игрового UI; station regression проверяет настоящие journals/captures.
/datum/netrun_packet_context
	var/operation_id
/datum/netrun_packet_context/New(kind, label, from_address, to_address, service_id, console, nonce, operation_id)
	src.operation_id = operation_id
/datum/netrun_packet_context/proc/capture(list/payload)
	return
/datum/netrun_packet_context/proc/queued(list/route, delay, announce)
	return
/datum/netrun_packet_context/proc/running()
	return
/datum/netrun_packet_context/proc/receive(list/route)
	return
/datum/netrun_packet_context/proc/finish(outcome, reason)
	return

/proc/netrun_communication_policy(datum/netrun_machine_service/source, destination_id)
	return FALSE
/datum/netrun_machine_transport
	var/list/services = list()

/datum/controller/subsystem/netrun
	var/list/nodes_by_address = list()
	var/datum/netrun_machine_transport/machine_transport = new
/datum/controller/subsystem/netrun/proc/get_machine_transport()
	return machine_transport
/datum/controller/subsystem/netrun/proc/wake_ready_delivery()
	return
/datum/external_mapping_fixture/proc/get_connected_levels(z)
	return list(1)

var/global/datum/controller/subsystem/netrun/SSnetrun = new
var/global/datum/external_mapping_fixture/SSmapping = new

/// Churn привязан к границе snapshot scan, а не к внешнему таймеру между async pump iterations.
/datum/netrun_external_core/external_fixture
	var/list/fixture_state
/datum/netrun_external_core/external_fixture/advance_snapshot(max_records, max_journal_records = 128)
	// Один node за scan сохраняет видимую services boundary даже при маленьком fixture мире.
	. = ..(snapshot_phase == "nodes" ? 1 : max_records, max_journal_records)
	if(snapshot_phase != "services" || !fixture_state || fixture_state["replaced"])
		return
	var/datum/netrun_machine_service/service = fixture_state["service"]
	var/datum/component/netrun_node/node = fixture_state["node"]
	var/obj/machinery/power/apc/host = fixture_state["host"]
	forget_service(service)
	SSnetrun.machine_transport.services -= "fixture"
	SSnetrun.nodes_by_address -= node.address
	on_node_unregistered(SSnetrun, node)
	node = new
	node.parent = host
	node.address = "APC-TEST"
	SSnetrun.nodes_by_address[node.address] = node
	on_node_registered(SSnetrun, node)
	service = new
	service.host = host
	service.node = node
	SSnetrun.machine_transport.services["fixture"] = service
	observe_service(service)
	fixture_state["node"] = node
	fixture_state["service"] = service
	fixture_state["replaced"] = TRUE

/world
	maxx = 8
	maxy = 8
	maxz = 1
	fps = 10

/// Token читается по локальному file path, а не передаётся в world.params или в логи.
/world/New()
	..()
	if(netrun_external_decimal_next("16777216") != "16777217" || netrun_external_decimal_next("18446744073709551615"))
		world.log << "EXTERNAL_FAIL decimal"
		shutdown()
		return
	var/obj/machinery/power/apc/host = new(locate(2, 2, 1))
	var/datum/component/netrun_node/node = new
	node.parent = host
	node.address = "APC-TEST"
	SSnetrun.nodes_by_address[node.address] = node
	var/datum/netrun_machine_service/service = new
	service.host = host
	service.node = node
	SSnetrun.machine_transport.services["fixture"] = service
	var/datum/netrun_external_core/external_fixture/client = new(SSnetrun)
	client.fixture_state = list("node" = node, "service" = service, "host" = host, "replaced" = FALSE)
	var/token = file2text(world.params["token_file"])
	if(!client.open(world.params["library"], world.params["port"], token))
		world.log << "EXTERNAL_FAIL configuration"
		shutdown()
		return
	var/list/old_key = client.node_key(node)
	var/deadline = world.time + 150
	while(world.time < deadline && !client.ready && client.state != "resync_required")
		client.tick(2)
		sleep(1)
	node = client.fixture_state["node"]
	service = client.fixture_state["service"]
	var/list/new_key = client.node_key(node)
	if(!client.ready || !client.fixture_state["replaced"] || old_key["id"] == new_key["id"] || client.resolve_node(old_key["id"], "1"))
		world.log << "EXTERNAL_FAIL synchronization [client.last_error || client.state]"
		client.close()
		shutdown()
		return
	// Настоящий executor проходит весь цикл через Host; handler получает исходный DM message ровно один раз.
	SSnetrun.external_mode = "external"
	SSnetrun.external_core = client
	var/datum/netrun_external_effects/machines/executor = new(SSnetrun)
	client.effects = executor
	var/list/outcomes = list()
	var/datum/netrun_machine_message/message = new(list("filters" = list(list("id" = "o2", "selected" = TRUE)), "empty" = list()), outcomes)
	if(!executor.dispatch(service, service, message))
		world.log << "EXTERNAL_FAIL machine_dispatch"
		client.close()
		shutdown()
		return
	deadline = world.time + 100
	while(world.time < deadline && !outcomes["delivered"] && !outcomes["failed"] && client.state != "resync_required")
		client.tick(4)
		sleep(1)
	if(outcomes["delivered"] != 1 || outcomes["failed"] || service.native_calls != 1)
		world.log << "EXTERNAL_FAIL machine_lifecycle [client.last_error || client.state]"
		client.close()
		shutdown()
		return
	// Полностью дренируем настоящий lifecycle перед idle heartbeat: никаких искусственных outcomes.
	deadline = world.time + 100
	while(world.time < deadline && (length(executor.request_commands) || length(executor.control_commands) || length(executor.inflight_commands) || length(client.inflight_frames) || client.pending_frame || client.sent_output_ack != client.ack_output_sequence))
		client.tick(4)
		sleep(1)
	if(length(executor.request_commands) || length(executor.control_commands) || length(executor.inflight_commands) || length(client.inflight_frames) || client.pending_frame || client.sent_output_ack != client.ack_output_sequence || !client.monitoring_negotiated)
		world.log << "EXTERNAL_FAIL telemetry_idle_barrier"
		client.close()
		shutdown()
		return
	var/previous_sample = client.monitor_sample_sequence
	var/idle_input = client.sent_input_sequence
	var/idle_applied = client.last_applied_sequence
	var/idle_ack = client.ack_output_sequence
	deadline = world.time + 100
	while(world.time < deadline && client.monitor_sample_sequence == previous_sample && client.state != "resync_required")
		client.tick(4)
		sleep(1)
	if(client.monitor_sample_sequence == previous_sample || client.sent_input_sequence != idle_input || client.last_applied_sequence != idle_applied || client.ack_output_sequence != idle_ack || client.host_metrics["sync_state"] != "ready" || client.host_metrics["node_count"] != 1 || client.host_metrics["service_count"] != 1 || !client.host_metrics["managed_bytes"])
		world.log << "EXTERNAL_FAIL telemetry_heartbeat [client.last_error || client.state]"
		client.close()
		shutdown()
		return
	world.log << "EXTERNAL_PASS monitor_heartbeat_without_application_progress"
	// Неизвестный effect нельзя считать обработанным и включать в cumulative output ACK.
	var/previous_ack = client.ack_output_sequence
	var/output = netrun_external_json_object(list("kind" = json_encode("output"), "world_epoch" = json_encode(client.world_epoch), "engine_epoch" = json_encode(client.engine_epoch), "binding_epoch" = json_encode(client.binding_epoch), "output_sequence" = json_encode(netrun_external_decimal_next(previous_ack)), "applied_sequence" = json_encode(client.last_applied_sequence), "results" = "\[{\"kind\":\"service_invocation\",\"binding_epoch\":[json_encode(client.binding_epoch)]}\]"))
	if(client.process_frame(output) || client.ack_output_sequence != previous_ack || client.last_error != "unsupported_output")
		world.log << "EXTERNAL_FAIL unsafe_ack"
		shutdown()
		return
	world.log << "EXTERNAL_PASS snapshot_churn_and_ack machine_lifecycle"
	client.close()
	sleep(2)
	shutdown()
