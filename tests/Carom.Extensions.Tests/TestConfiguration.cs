using System.Runtime.CompilerServices;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Configures test environment before any tests run.
    /// Increases state store sizes to prevent LRU eviction during parallel test execution.
    /// </summary>
    public class TestConfiguration
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            // Increase store sizes to prevent eviction during parallel tests
            CushionStore.MaxSize = 10000;
            ThrottleStore.MaxSize = 10000;
            CompartmentStore.MaxSize = 10000;
        }
    }
}
