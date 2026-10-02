// Stands in for DarkHaven.Loader when GameLauncherTests launch through the development guard on Windows (on Linux a
// /bin/sh script does it). Like that script it prints "arg <value>" per argument and "env <NAME>=<value>" - or
// "(unset)" - per name in watch.txt, then runs script.txt: the few things the tests' scripts do.
//
//   echo <text>      a line on stdout
//   stderr <text>    a line on stderr
//   sleep <seconds>
//   touch <path>     create the file
//   exit <code>

var dir = AppContext.BaseDirectory;

foreach (var arg in args)
    Console.WriteLine($"arg {arg}");
foreach (var name in File.ReadAllLines(Path.Combine(dir, "watch.txt")))
    Console.WriteLine($"env {name}={Environment.GetEnvironmentVariable(name) ?? "(unset)"}");
Console.Out.Flush();

foreach (var line in File.ReadAllLines(Path.Combine(dir, "script.txt")))
{
    var space = line.IndexOf(' ');
    var (command, rest) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    switch (command)
    {
        case "echo":
            Console.WriteLine(rest);
            Console.Out.Flush();
            break;
        case "stderr":
            Console.Error.WriteLine(rest);
            Console.Error.Flush();
            break;
        case "sleep":
            Thread.Sleep(TimeSpan.FromSeconds(double.Parse(rest, System.Globalization.CultureInfo.InvariantCulture)));
            break;
        case "touch":
            File.WriteAllText(rest, "");
            break;
        case "exit":
            return int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture);
        case "":
            break;
        default:
            Console.Error.WriteLine($"fake loader: unknown command '{command}'");
            return 99;
    }
}
return 0;
