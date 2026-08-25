using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BladeControl.Hardware.Windows")]
[assembly: InternalsVisibleTo("BladeControl.Thermal")]
[assembly: InternalsVisibleTo("BladeControl.Razer.Tests")]
[assembly: InternalsVisibleTo("BladeControl.Runtime.Tests")]

// Service.Tests drives the real named-pipe server end to end, which needs a real
// BladeRuntime behind it and therefore the same fakes Runtime.Tests uses. The fakes are shared
// rather than copied, so they need the same visibility.
[assembly: InternalsVisibleTo("BladeControl.Service.Tests")]
