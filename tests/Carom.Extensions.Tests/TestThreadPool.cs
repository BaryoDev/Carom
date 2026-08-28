// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Carom.Extensions.Tests
{
    internal static class TestThreadPool
    {
        // The stress tests block many pool threads at once (Barrier(100) in the
        // real-world suite, dedicated waits elsewhere). On a 2-core CI runner the
        // pool grows past its minimum at roughly one thread per second, so with two
        // target frameworks running concurrently, a wall-clock test can starve for
        // its whole window: the 1.7.0 publish failed with the spin-loop test
        // recording 0 completions in 5 seconds. Raising the floor makes thread
        // injection immediate for the test process only.
        [ModuleInitializer]
        internal static void RaiseMinThreads()
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            ThreadPool.SetMinThreads(Math.Max(worker, 256), Math.Max(io, 256));
        }
    }
}
