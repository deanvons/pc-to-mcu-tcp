using System.Net;
using System.Net.Sockets;
using System.Text;

const int Port = 5000;

// TcpListener is the "I am a server" object. It does two things:
// binds to a port (claims that number with the OS) and then accepts
// incoming connections.
//
// IPAddress.Any means "bind to every network interface on this machine" —
// loopback, Ethernet, Wi-Fi, all of them. That's what lets the same binary
// answer both 127.0.0.1 during local testing and 192.168.10.10 over the
// cable later. If you wanted to restrict it to loopback only, you'd pass
// IPAddress.Loopback instead.
var listener = new TcpListener(IPAddress.Any, Port);

// Start() performs the bind and puts the socket into the listening state.
// This is the call that throws if another process already owns port 5000
// ("address already in use"). Nothing is accepted yet.
listener.Start();
Console.WriteLine($"Mock server listening on port {Port}. Ctrl+C to stop.");

// Outer loop: serve one client, then go back and wait for the next.
while (true)
{
    // Wait for a client to connect. See section 1.3 on what this await
    // actually does — it is not a busy-wait, and it is not a blocked thread.
    //
    // The returned TcpClient represents this one connection. `using` makes
    // sure the socket is closed when we fall out of the loop body, whether
    // that's a clean disconnect or an exception.
    using TcpClient client = await listener.AcceptTcpClientAsync();
    Console.WriteLine($"Client connected from {client.Client.RemoteEndPoint}");

    // NetworkStream is the raw byte pipe for this connection. TCP gives you
    // an ordered stream of bytes and nothing more — it has no concept of
    // where one message ends and the next begins. That's our job, and it's
    // why the protocol uses newlines as delimiters.
    using NetworkStream stream = client.GetStream();

    // StreamReader/StreamWriter sit on top of the byte pipe and do the
    // newline splitting for us. ASCII because the firmware has no business
    // dealing with multi-byte encodings.
    using var reader = new StreamReader(stream, Encoding.ASCII);

    // AutoFlush = true means WriteLine pushes the bytes out immediately.
    // Without it, responses sit in a buffer and the client appears to hang —
    // a classic and very confusing bug.
    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

    // On Windows, WriteLine defaults to "\r\n". The firmware sends and
    // expects a bare "\n", so pin it explicitly rather than relying on
    // whatever the platform default happens to be.
    writer.NewLine = "\n";

    try
    {
        string? line;

        // ReadLineAsync accumulates bytes until it sees a newline, then
        // hands back the line without the terminator. It returns null when
        // the client closes the connection — that's the loop's exit.
        while ((line = await reader.ReadLineAsync()) != null)
        {
            // Trim strips any stray carriage return (if someone tests with
            // PuTTY, which sends "\r\n") plus surrounding whitespace.
            line = line.Trim();

            // A bare newline is not an error, just nothing to do.
            if (line.Length == 0) continue;

            Console.WriteLine($"  RX: {line}");

            // The entire command set, for now. The point of this exercise is
            // the plumbing, not the commands.
            string response = line switch
            {
                "PING" => "PONG",
                "ID"   => "OK MOCKSERVER v0.1",
                _      => "ERR UNKNOWN_COMMAND"
            };

            Console.WriteLine($"  TX: {response}");

            // Exactly one response line per request. The client is waiting
            // for it, so never return zero lines and never return two.
            await writer.WriteLineAsync(response);
        }
    }
    catch (IOException ex)
    {
        // Cable yanked, client process killed, board reset. Not a bug —
        // just log it and go back to accepting.
        Console.WriteLine($"  Connection dropped: {ex.Message}");
    }

    Console.WriteLine("Client disconnected.");
}
