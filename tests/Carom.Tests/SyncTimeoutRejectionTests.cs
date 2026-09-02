// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Issue #36: the synchronous Shot overloads used to silently drop a Bounce
    /// timeout. WithTimeout(Zero) already throws at configuration time, so a valid
    /// timeout that is quietly ignored is inconsistent. The sync path now rejects
    /// a timeout-carrying Bounce with InvalidOperationException.
    /// </summary>
    public class SyncTimeoutRejectionTests
    {
        private static readonly TimeSpan SomeTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public void Shot_FuncT_with_Bounce_timeout_throws()
        {
            var bounce = Bounce.Times(0).WithTimeout(SomeTimeout);

            var ex = Assert.Throws<InvalidOperationException>(() => Carom.Shot(() => 42, bounce));
            Assert.Contains("ShotAsync", ex.Message);
            Assert.Contains("WithTimeout", ex.Message);
        }

        [Fact]
        public void Shot_Action_with_Bounce_timeout_throws()
        {
            var bounce = Bounce.Times(0).WithTimeout(SomeTimeout);

            Assert.Throws<InvalidOperationException>(() => Carom.Shot(() => { }, bounce));
        }

        [Fact]
        public void Shot_FuncT_with_generic_Bounce_timeout_throws()
        {
            var bounce = Bounce.For<int>(0).WithTimeout(SomeTimeout);

            Assert.Throws<InvalidOperationException>(() => Carom.Shot(() => 42, bounce));
        }

        [Fact]
        public void Shot_with_Bounce_timeout_does_not_invoke_the_action()
        {
            var bounce = Bounce.Times(3).WithTimeout(SomeTimeout);
            var invoked = false;

            Assert.Throws<InvalidOperationException>(() => Carom.Shot(() => { invoked = true; }, bounce));

            Assert.False(invoked);
        }

        [Fact]
        public void Shot_FuncT_without_timeout_still_works()
        {
            var attempts = 0;
            var bounce = Bounce.Times(2).WithDelay(TimeSpan.FromMilliseconds(1));

            var result = Carom.Shot(() =>
            {
                attempts++;
                if (attempts < 2) throw new InvalidTimeZoneException("transient");
                return 42;
            }, bounce);

            Assert.Equal(42, result);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public void Shot_Action_without_timeout_still_works()
        {
            var invoked = false;

            Carom.Shot(() => { invoked = true; }, Bounce.Times(0));

            Assert.True(invoked);
        }

        [Fact]
        public void Shot_generic_Bounce_without_timeout_still_works()
        {
            var attempts = 0;
            var bounce = Bounce.For<int>(2).WithDelay(TimeSpan.FromMilliseconds(1)).WhenResult(r => r < 0);

            var result = Carom.Shot(() =>
            {
                attempts++;
                return attempts < 2 ? -1 : 42;
            }, bounce);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_with_Bounce_timeout_is_unaffected()
        {
            var bounce = Bounce.Times(0).WithTimeout(SomeTimeout);

            var result = await Carom.ShotAsync(() => Task.FromResult(42), bounce);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_with_generic_Bounce_timeout_is_unaffected()
        {
            var bounce = Bounce.For<int>(0).WithTimeout(SomeTimeout);

            var result = await Carom.ShotAsync(() => Task.FromResult(42), bounce);

            Assert.Equal(42, result);
        }
    }
}
