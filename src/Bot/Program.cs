using System;
using Bot;
using RedUtils;

// Parameter variants for experiments: STARDUST_TUNE="Type.Field=value,...".
foreach (string line in Tuning.Apply(Environment.GetEnvironmentVariable("STARDUST_TUNE"),
             typeof(Stardust).Assembly, typeof(Car).Assembly))
    Console.WriteLine($"stardust tune {line}");

Stardust bot = new Stardust();
// Enable match communications (true) so teammates can coordinate shot claims
bot.Run(true, true);
