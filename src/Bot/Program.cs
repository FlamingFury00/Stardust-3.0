using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Bot;
using RedUtils;

StardustOptions options = StardustOptions.FromArguments(args,
    argument => Console.WriteLine($"stardust: ignoring unknown argument {argument}"));
ApplyTuning(options.Tune);

Stardust bot = new Stardust(options: options);
// Enable match communications (true) so teammates can coordinate shot claims
bot.Run(true, true);

// Parameter variants for experiments: --tune=Type.Field=value,... The fields are found by
// reflection, which the native (AOT) build does not support, so there the argument is ignored.
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only reached with dynamic code, i.e. in JIT builds.")]
static void ApplyTuning(string assignments)
{
    if (string.IsNullOrWhiteSpace(assignments))
        return;
    if (!RuntimeFeature.IsDynamicCodeSupported)
    {
        Console.WriteLine("stardust tune ignored: native build");
        return;
    }
    foreach (string line in Tuning.Apply(assignments, typeof(Stardust).Assembly, typeof(Car).Assembly))
        Console.WriteLine($"stardust tune {line}");
}
