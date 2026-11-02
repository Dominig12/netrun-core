// Изолированный BYOND516 smoke harness: не включает станцию и не создаёт игровые эффекты.
/world/New()
    ..()
    var/list/settings = world.params
    var/library = settings["library"]
    var/port = settings["port"]
    var/open_result = call_ext(library, "netrun_open")("127.0.0.1", port, "8", "1048576", "1048576")
    var/list/open_status = json_decode(open_result)
    if(open_status["code"] != "accepted")
        world.log << "NATIVE_FAIL open"
        shutdown()
        return
    var/deadline = world.time + 50
    var/sent = 0
    var/received = 0
    while(world.time < deadline)
        var/list/status = json_decode(call_ext(library, "netrun_status")())
        if(status["state"] == "connected" && !sent)
            var/list/enqueued = json_decode(call_ext(library, "netrun_try_enqueue")("{\"byond\":516}"))
            sent = enqueued["code"] == "accepted"
        var/list/polled = json_decode(call_ext(library, "netrun_poll")("8", "1048576"))
        var/list/frames = polled["frames"]
        if(length(frames))
            if(frames[1] == "{\"byond\":516}")
                received = 1
            break
        sleep(1)
    call_ext(library, "netrun_close")()
    deadline = world.time + 20
    while(world.time < deadline)
        var/list/status = json_decode(call_ext(library, "netrun_status")())
        if(status["state"] == "closed")
            break
        sleep(1)
    world.log << (received ? "NATIVE_PASS BYOND516" : "NATIVE_FAIL delivery")
    shutdown()
