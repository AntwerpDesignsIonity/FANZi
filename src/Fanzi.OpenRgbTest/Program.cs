using OpenRGB.NET;
using System;

try
{
    Console.WriteLine("=== OpenRGB Connection Test ===");
    Console.WriteLine($"Connecting to localhost:6742...");

    var client = new OpenRgbClient(
        ip: "127.0.0.1",
        port: 6742,
        name: "FANZI-TEST",
        autoConnect: false,
        protocolVersionNumber: 4);

    client.Connect();
    Console.WriteLine("Connect() succeeded!");

    Console.WriteLine("Connect() OK — fetching devices...");

    var devices = client.GetAllControllerData();
    Console.WriteLine($"Found {devices.Length} device(s):");

    for (int i = 0; i < devices.Length; i++)
    {
        var d = devices[i];
        Console.WriteLine($"  [{i}] {d.Name} ({d.Type}) — {d.Leds.Length} LEDs, {d.Zones.Length} zones");
        Console.WriteLine($"       Active mode: {d.ActiveMode?.Name ?? "?"} (index {d.ActiveModeIndex})");

        // Try setting Direct mode
        try
        {
            for (int m = 0; m < d.Modes.Length; m++)
            {
                string modeName = d.Modes[m].Name ?? "";
                Console.WriteLine($"       Mode [{m}]: {modeName}");
            }

            client.SetCustomMode(i);
            Console.WriteLine($"       SetCustomMode() OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       SetCustomMode() failed: {ex.Message}");
        }

        // Try sending a colour
        try
        {
            var colors = new OpenRGB.NET.Color[d.Leds.Length];
            for (int k = 0; k < colors.Length; k++)
                colors[k] = new OpenRGB.NET.Color(0, 100, 255); // Blue
            client.UpdateLeds(i, colors);
            Console.WriteLine($"       UpdateLeds() OK — set all LEDs to blue");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       UpdateLeds() failed: {ex.Message}");
        }
    }

    client.Dispose();
    Console.WriteLine("=== Test complete ===");
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
