# TCP Networking Exercise — Setup Guide

**Goal:** get a C# client on a Windows PC talking to a TCP server running in microcontroller firmware, over a direct Ethernet link. No diagnostic features yet — this exercise is only about wiring, addressing, and proving a connection.

**Roles:**

| Side            | Role                      | Why                                                      |
| --------------- | ------------------------- | -------------------------------------------------------- |
| Microcontroller | TCP **server** — listens  | It's the device being interrogated; it waits to be asked |
| Windows C# app  | TCP **client** — connects | The operator initiates the session                       |

**Fixed parameters for this exercise:**

| Parameter   | Value                                      |
| ----------- | ------------------------------------------ |
| Transport   | TCP over IPv4                              |
| Port        | 5000                                       |
| Micro IP    | 192.168.10.11                              |
| Windows IP  | 192.168.10.10                              |
| Subnet mask | 255.255.255.0 (/24)                        |
| Gateway     | none (leave blank)                         |
| Framing     | one message per line, terminated with `\n` |
| Encoding    | ASCII                                      |

> **Why 192.168.10.x and not 192.168.1.x?** Most home and office routers already use 192.168.0.x or 192.168.1.x. If the PC's Wi-Fi is on the same subnet as its Ethernet adapter, Windows gets ambiguous routes and the link behaves unpredictably. Picking an unusual third octet avoids the whole class of problem.

---

## Part 1 — Verify both ends on localhost (no hardware)

Build these two console apps first. They let you prove the client works before any board exists, and later they double as a reference implementation for the students.

### 1.1 Mock server

```bash
dotnet new console -n MockServer
cd MockServer
```

### 1.2 Client

```bash
dotnet new console -n DiagClient
cd DiagClient
```

### 1.3 What that `await` on accept actually does

Short answer: execution stops there until a client connects, but nothing is _blocked_ in the sense that matters.

`AcceptTcpClientAsync` returns immediately with an incomplete `Task`. The `await` hands control back to the caller and the thread is returned to the thread pool — it's free to do other work, and in this console app it simply goes idle. When the OS completes the TCP handshake with an incoming client, it signals an I/O completion, the task completes, and the rest of the loop body resumes on a pool thread. No thread sat spinning, and no thread sat parked.

For _this_ program the observable behaviour is identical to the blocking `listener.AcceptTcpClient()`. The window is dark either way. The difference only pays off when there's something else to do — a UI to keep responsive, a cancellation token to honour on Ctrl+C, or many connections in flight. Which is exactly the situation you'll be in when this logic moves into the Windows Forms app: block there and the UI freezes.

Three related points worth knowing:

**Ctrl+C is your only exit.** There's no cancellation token wired up, so the app cannot be asked to stop gracefully while it's waiting. That's fine for a test harness. For anything longer-lived, pass a token: `AcceptTcpClientAsync(cts.Token)`.

**Only one client at a time is served.** While execution is inside the inner read loop, `AcceptTcpClientAsync` isn't being called. A second client connecting during that window doesn't get refused — the OS holds it in the accept backlog queue, and it looks connected from its own side while receiving no responses. It's only picked up once the first client disconnects. This is deliberate: it mirrors the single-connection firmware. But it explains a confusing symptom if two students point clients at the same board.

**The inner `await reader.ReadLineAsync()` behaves the same way.** It suspends until a full line arrives or the connection closes. Unlike the client, the server has no timeout set, so it will wait indefinitely for a client that connects and then says nothing. Acceptable for a mock; not acceptable for production.

### 1.4 Run the loopback test

Two terminals:

```
# Terminal 1
cd MockServer && dotnet run

# Terminal 2
cd DiagClient && dotnet run 127.0.0.1 5000
```

Type `PING`, expect `PONG`. Type `ID`, expect `OK MOCKSERVER v0.1`. Type `HELLO`, expect `ERR UNKNOWN_COMMAND`.

If that works, your client is correct. When the real hardware arrives, the **only** thing that changes is the argument: `dotnet run 192.168.10.11 5000`.

### 1.5 If port 5000 is refused

Windows reserves blocks of ports for Hyper-V, WSL, and Docker. Check before blaming your code:

```
netsh interface ipv4 show excludedportrange protocol=tcp
```

If 5000 falls inside an excluded range, pick something outside it — 5001, 7000, and 9000 are usually clear — and update this document's parameter table so students use the same number.

---

## Part 2 — Windows static IP and ping verification

### 2.1 Cabling

Run a standard Ethernet cable directly between the PC's Ethernet port and the board's RJ45 jack. No switch or router needed. Almost all modern NICs and PHYs support auto-MDIX, so a crossover cable is not required; if you get no link light at all, and only then, try a crossover.

Confirm the link light on the PC's port is lit before going any further. No link light means a cable, power, or PHY problem, and nothing above it will work.

### 2.2 Assign the static IP

Your C# app cannot set the machine's own address — that's an OS-level setting. Do it once, manually.

**Via the GUI:**

1. Settings → Network & Internet → Ethernet
2. Click the adapter for the direct link (not Wi-Fi)
3. Under **IP assignment**, click **Edit**
4. Switch from **Automatic (DHCP)** to **Manual**
5. Turn **IPv4** on
6. IP address: `192.168.10.10`
7. Subnet mask: `255.255.255.0`
8. Gateway: leave blank
9. DNS: leave blank
10. Save

**Or via an elevated PowerShell:**

```powershell
# Find the adapter name first
Get-NetAdapter

# Then (substitute the real InterfaceAlias)
New-NetIPAddress -InterfaceAlias "Ethernet" `
                 -IPAddress 192.168.10.10 `
                 -PrefixLength 24
```

To put it back on DHCP afterwards:

```powershell
Remove-NetIPAddress -InterfaceAlias "Ethernet" -Confirm:$false
Set-NetIPInterface -InterfaceAlias "Ethernet" -Dhcp Enabled
```

### 2.3 Confirm the address took

```
ipconfig
```

Look at the Ethernet adapter. You want `192.168.10.10`. If you see something beginning `169.254.`, that's APIPA — Windows failed to get an address and invented one, meaning the manual setting didn't apply to the adapter you think it did.

### 2.4 Ping the board

```
ping 192.168.10.11
```

Success looks like four replies with a sub-millisecond time.

**What ping does and doesn't prove.** Ping uses ICMP, which lwIP answers all by itself as soon as the interface is up. A successful ping proves the cable, the PHY, the addressing, and the subnet mask are all correct. It says nothing about whether your TCP server is listening. Those are two separate things, and students will conflate them.

### 2.5 Common ping failures

| Symptom                        | Likely cause                                                                                            |
| ------------------------------ | ------------------------------------------------------------------------------------------------------- |
| `Destination host unreachable` | Addresses are on different subnets. Check both masks are 255.255.255.0 and both IPs share `192.168.10.` |
| `Request timed out`            | Board's interface isn't up, or its IP differs from what the firmware sets                               |
| PC shows `169.254.x.x`         | Static IP applied to the wrong adapter, or not saved                                                    |
| No link light                  | Cable, board power, or PHY init                                                                         |

### 2.6 Windows Firewall

Not needed for this exercise, since Windows is the client and only makes outbound connections. But note it for later: if you ever flip the roles and run a **listener** on Windows, the firewall will silently block inbound connections until you allow it:

```powershell
New-NetFirewallRule -DisplayName "Diag TCP 5000" `
                    -Direction Inbound -Protocol TCP `
                    -LocalPort 5000 -Action Allow
```

The same applies to pinging _the PC_ from elsewhere — Windows drops inbound ICMP echo by default.

---

## Part 3 — TCP server in firmware

**Your task:** bring up the Ethernet interface with a static IP, listen on TCP port 5000, and answer two commands. That's all. No sensors, no diagnostics — just get the connection working end to end.

### 3.1 What you're building

```
   Windows PC                          Microcontroller
   192.168.10.10                       192.168.10.11
   ┌──────────────┐                    ┌──────────────┐
   │  DiagClient  │                    │  Your firmware│
   │   (client)   │ ──── TCP:5000 ──── │   (server)    │
   │   connects   │                    │    listens    │
   └──────────────┘                    └──────────────┘
          │                                    │
     Windows TCP/IP stack                lwIP stack
          │                                    │
          └────────── Ethernet cable ──────────┘
```

### 3.2 Choosing an lwIP API

lwIP offers two programming interfaces. Pick based on whether you have an RTOS:

| API                          | Use when                  | Style                                                                          |
| ---------------------------- | ------------------------- | ------------------------------------------------------------------------------ |
| **Raw / callback** (`tcp_*`) | Bare metal, no RTOS       | Register callbacks, never block, call `sys_check_timeouts()` in your main loop |
| **Netconn / sockets**        | FreeRTOS, Zephyr, ThreadX | Blocking `accept()` / `recv()` in a task, looks like BSD sockets               |

The raw API is shown below because it's the common bare-metal case. If you're on an RTOS, the sockets API is considerably easier — use it.

### 3.3 Bring up the interface with a static IP

Unlike Windows, this is all in code. In your network init, before anything else:

```c
#include "lwip/init.h"
#include "lwip/netif.h"
#include "lwip/timeouts.h"
#include "netif/ethernet.h"

/* The netif ("network interface") struct is lwIP's representation of one
   adapter. It must live for the lifetime of the program — hence static,
   not a local. A very common beginner bug is declaring it on the stack
   inside init and watching everything break once that frame is gone. */
static struct netif g_netif;

void network_init(void)
{
    ip4_addr_t ipaddr, netmask, gw;

    /* Brings up lwIP's internal state: memory pools, protocol control
       block lists, timers. Call exactly once, before anything else lwIP.
       (Raw API only. Under an RTOS you'd call tcpip_init instead.) */
    lwip_init();

    /* This is the equivalent of the manual IP setting you did on Windows —
       except here it's compiled in rather than clicked in. There's no DHCP
       server on a direct cable, so a static address is the only option.
       (lwIP does have dhcp_start() if a router is present.) */
    IP4_ADDR(&ipaddr,  192, 168, 10, 11);

    /* The mask defines which addresses count as "on my local wire".
       255.255.255.0 means the first three octets must match, so
       192.168.10.10 is local and reachable directly. Get this wrong and
       ping returns "destination host unreachable" — the stack decides the
       PC is somewhere else entirely and looks for a gateway to forward to. */
    IP4_ADDR(&netmask, 255, 255, 255,  0);

    /* 0.0.0.0 = no gateway. Correct for a two-device direct link: there is
       nowhere else to route to. */
    IP4_ADDR(&gw,        0,   0,  0,  0);

    /* netif_add wires the addresses to a driver and inserts the interface
       into lwIP's list. The two function pointers are the important part:

         ethernetif_init  - your port's driver glue: initialises the MAC and
                            PHY, sets the hardware address, hooks up the
                            transmit function. This differs per vendor; on
                            STM32 with CubeMX it's generated for you.
         ethernet_input   - lwIP's own function for the other direction.
                            Received frames are handed to it and it
                            dispatches them to ARP, IP, and onward.

       The NULL is a user-data pointer, unused here. */
    netif_add(&g_netif, &ipaddr, &netmask, &gw,
              NULL, ethernetif_init, ethernet_input);

    /* With only one interface this is mostly a formality, but it tells lwIP
       which interface to use for traffic with no more specific route. */
    netif_set_default(&g_netif);

    /* Marks the interface administratively up. Until this is called the
       stack will not send or receive anything — including ICMP, so a
       missing netif_set_up is a prime suspect when ping fails. */
    netif_set_up(&g_netif);
}
```

At this point — before you write a single line of TCP code — flash it and have the PC ping `192.168.10.11`. lwIP answers ICMP on its own. **If ping doesn't work, stop and fix that first.** Every problem beyond this point is much harder to diagnose with a broken link underneath it.

### 3.3a Checkpoint: ping only

To flash and test at this stage you need a `main`. This one brings up the interface and keeps the stack running, with no TCP server at all:

```c
int main(void)
{
    hardware_init();

    /* Interface only. No TCP server yet: that comes in 3.4. */
    network_init();

    while (1) {
        /* Pull received Ethernet frames out of the MAC and hand them to
           lwIP. Ping still needs this: without it, neither the PC's ARP
           request nor its ICMP echo request ever reaches the stack. */
        ethernetif_input(&g_netif);

        /* Service lwIP's timers (ARP table ageing among them). The raw API
           has no thread of its own, so this is the only way time passes
           for the stack. */
        sys_check_timeouts();
    }
}
```

Flash it, then from the PC:

```
ping 192.168.10.11
```

Replies mean the cable, PHY, driver, addressing, and subnet mask are all correct — and you haven't written any TCP yet. If you get replies here, any later failure to connect is in your TCP code, not the link.

**If ping fails at this checkpoint** and Part 2's table doesn't explain it, check these three before anything else:

- **Link state.** In lwIP 2.x the stack only sends on an interface that is both _up_ and _link-up_. `netif_set_up` covers the first; some vendor drivers set the second for you (STM32 CubeMX does, via `ethernet_link_check_state`), but bare ports often don't. If the board receives the ping but never replies, add `netif_set_link_up(&g_netif);` directly after `netif_set_up` in `network_init`.
- **`sys_now()`.** With `NO_SYS=1`, `sys_check_timeouts()` calls `sys_now()`, which you must implement to return milliseconds from a real tick source such as SysTick. A stub that returns 0 compiles fine and breaks the stack's timers in subtle ways.
- **`lwipopts.h`.** `LWIP_ICMP` and `LWIP_ARP` must be enabled. Both default to on, but trimmed configurations sometimes turn them off.

### 3.4 Listen on port 5000

> **PLACEHOLDER — content delivered via slide deck.**
>
> Covers: the lwIP raw-API TCP server (`diag_server_init`, `on_accept`, `on_recv`), line assembly until `\n`, `send_line` with `tcp_write` + `tcp_output`, and handling `PING` / `ID`.
>
> The end result must provide `diag_server_init()`, which is called from `main` in 3.5.

### 3.5 Main loop

Start from your checkpoint `main` from 3.3a. The only change is one new line: call `diag_server_init()` after `network_init()`. Ping keeps working exactly as before; the new line is what makes the TCP connection work.

The raw API is callback-driven and never blocks. Your loop must service lwIP's timers or nothing works:

```c
int main(void)
{
    hardware_init();

    /* Order matters: the interface must exist before anything binds to it. */
    network_init();
    diag_server_init();     /* NEW since 3.3a: start the TCP server */

    while (1) {
        /* Pull any received Ethernet frames out of the MAC and hand them to
           lwIP, which runs your callbacks from inside this call. On some
           ports this is driven by an interrupt instead — check your vendor's
           example. Either way, if nothing feeds the stack, nothing happens. */
        ethernetif_input(&g_netif);

        /* lwIP has no thread of its own in the raw API, so it cannot keep
           time by itself. This is where retransmission timers, ARP table
           ageing, and TCP keepalives actually get serviced. Miss it and the
           symptom is bizarre: connections establish, then fail to recover
           from any lost packet. Call it every loop iteration. */
        sys_check_timeouts();
    }
}
```

### 3.6 Acceptance criteria

You're done when all four pass:

1. `ping 192.168.10.11` from the PC returns replies
2. `dotnet run 192.168.10.11 5000` connects without error
3. `PING` returns `PONG`
4. `ID` returns a line beginning `OK`

### 3.7 Things that will trip you up

- **Newline handling.** The C# client sends `\n`, not `\r\n`. If you test with PuTTY instead, it sends `\r\n` — hence the trim in `handle_command`.
- **Forgetting `tcp_recved`.** Without it, the receive window closes and the connection stalls after a few messages.
- **Forgetting `pbuf_free`.** Leaks buffers until lwIP runs out and silently stops receiving.
- **Assuming one recv equals one command.** TCP is a stream with no message boundaries. That's precisely why we chose a newline delimiter — the accumulate-until-newline loop is not optional.
- **Blocking in a callback.** In the raw API, callbacks run from the network context. A long delay inside one will break the stack's timing.
- **Ping works but connect fails.** That's the expected symptom of `diag_server_init()` never being called, or being called before `network_init()`.

---

## Appendix — Protocol specification template

Copy this page, fill in the blanks, and hand it out with the assignment. If everyone invents their own message format, nothing interoperates.

---

### Diagnostic Protocol Specification

**Version:** 0.1
**Date:** ****\_\_\_\_****
**Author:** ****\_\_\_\_****

#### Transport

|                        |                                       |
| ---------------------- | ------------------------------------- |
| Protocol               | TCP over IPv4                         |
| Port                   | 5000                                  |
| Server (listens)       | Microcontroller, 192.168.10.11        |
| Client (connects)      | Windows diagnostic app, 192.168.10.10 |
| Concurrent connections | 1                                     |
| Idle timeout           | none                                  |

#### Framing

- One message per line.
- Messages are terminated by a single line feed, `0x0A`.
- Carriage returns preceding the line feed are tolerated and stripped.
- Maximum line length: 128 bytes including the terminator. Longer lines are rejected.
- Character encoding: ASCII, 7-bit.

#### Message exchange

Strictly request–response. The client sends one command and waits for exactly one response line before sending the next. The server never sends unsolicited messages.

#### Command format

```
<COMMAND> [argument] [argument] ...
```

Commands are uppercase. Arguments are separated by single spaces.

#### Response format

```
OK [payload]          success
ERR <REASON>          failure
```

#### Defined commands

| Command | Arguments | Success response      | Notes                   |
| ------- | --------- | --------------------- | ----------------------- |
| `PING`  | none      | `PONG`                | Liveness check          |
| `ID`    | none      | `OK <name> <version>` | Identifies the firmware |
|         |           |                       |                         |
|         |           |                       |                         |
|         |           |                       |                         |

#### Defined error reasons

| Reason            | Meaning                                  |
| ----------------- | ---------------------------------------- |
| `UNKNOWN_COMMAND` | Command verb not recognised              |
| `LINE_TOO_LONG`   | Request exceeded the maximum line length |
| `BAD_ARGUMENT`    | Argument missing or malformed            |
|                   |                                          |

#### Example session

```
> PING
< PONG
> ID
< OK MCU-DIAG v0.1
> WOBBLE
< ERR UNKNOWN_COMMAND
```

#### Open questions

- ***
- ***
