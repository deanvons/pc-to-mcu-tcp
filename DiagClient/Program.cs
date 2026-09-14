using System.Net.Sockets;
using System.Text;

// Default to loopback so `dotnet run` with no arguments does the local test.
// Pass the board's address later: dotnet run 192.168.10.11 5000
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 5000;

using var client = new TcpClient();

// Connect() performs the TCP three-way handshake. It throws
// SocketException if nothing is listening on that port ("connection
// refused") or if the host can't be reached at all ("timed out").
//
// Those two failures mean different things and it's worth learning to tell
// them apart: "refused" means the machine is there and answered, but no
// process owns the port — so your addressing is fine and your server isn't
// running. "Timed out" means nothing answered at all — that's a cable,
// subnet, or firewall problem.
client.Connect(host, port);
Console.WriteLine($"Connected to {host}:{port}. Type a command, or 'quit' to exit.");

using NetworkStream stream = client.GetStream();
using var reader = new StreamReader(stream, Encoding.ASCII);
using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

// Match the firmware: bare "\n", not the Windows default "\r\n".
writer.NewLine = "\n";

// Don't wait forever if the far end accepts the connection but never
// answers — a very common firmware bug, where tcp_write was called but
// tcp_output was forgotten.
//
// IMPORTANT: ReadTimeout only applies to *synchronous* reads. NetworkStream
// ignores it entirely for ReadAsync/ReadLineAsync, which is why this client
// is written synchronously. If you convert it to async later, this line
// silently stops doing anything and you'll hang forever on a dead server.
// Use ReadLineAsync(CancellationToken) instead if you go that route.
stream.ReadTimeout = 5000;

while (true)
{
    Console.Write("> ");

    // Blocks on keyboard input. Null means stdin was closed (Ctrl+Z / piped
    // input ending), which we treat the same as "quit".
    string? command = Console.ReadLine();
    if (command is null || command.Equals("quit", StringComparison.OrdinalIgnoreCase))
        break;
    if (command.Length == 0) continue;

    // Send the command plus its newline terminator. The newline is what
    // tells the far end the message is complete — without it the server
    // sits waiting for more bytes and you get a five-second timeout.
    writer.WriteLine(command);

    try
    {
        // Strictly one response line per command, so a single ReadLine is
        // the whole reply. Returns null if the server closed the connection.
        string? response = reader.ReadLine();
        if (response is null)
        {
            Console.WriteLine("Server closed the connection.");
            break;
        }
        Console.WriteLine($"< {response}");
    }
    catch (IOException)
    {
        // ReadTimeout expired. The connection is now in an unknown state —
        // a late reply may still arrive and desynchronise every subsequent
        // request/response pair — so in a real tool you'd tear down and
        // reconnect here rather than carrying on.
        Console.WriteLine("Timed out waiting for a response.");
    }
}
