// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Text;

namespace Carom.Extensions
{
    /// <summary>
    /// One behavioural configuration field: its name and both values, durations
    /// normalised to ticks so the comparison is a plain long compare.
    /// </summary>
    internal readonly struct ConfigField
    {
        public readonly string Name;
        public readonly long Existing;
        public readonly long Requested;
        public readonly bool IsDuration;

        public ConfigField(string name, int existing, int requested)
        {
            Name = name;
            Existing = existing;
            Requested = requested;
            IsDuration = false;
        }

        public ConfigField(string name, TimeSpan existing, TimeSpan requested)
        {
            Name = name;
            Existing = existing.Ticks;
            Requested = requested.Ticks;
            IsDuration = true;
        }

        public bool Matches => Existing == Requested;

        public string FormatExisting() => Format(Existing);

        public string FormatRequested() => Format(Requested);

        private string Format(long value) =>
            IsDuration ? TimeSpan.FromTicks(value).ToString() : value.ToString();
    }

    /// <summary>
    /// Shared conflict rule for the static state stores: a key registered twice must carry the
    /// same behavioural configuration, or the second registration is rejected.
    /// </summary>
    /// <remarks>
    /// One rule rather than three copies on purpose: each store must enforce it on both the
    /// sequential path and the lost-race path, and a rule written per store is a rule that drifts.
    /// Fields are passed by value so the matching path allocates nothing.
    /// </remarks>
    internal static class StoreConflictHelper
    {
        public static void ThrowIfConflicting(string keyKind, string key, ConfigField f1, ConfigField f2)
        {
            if (f1.Matches && f2.Matches)
            {
                return;
            }

            ThrowConflict(keyKind, key, new[] { f1, f2 });
        }

        public static void ThrowIfConflicting(string keyKind, string key, ConfigField f1, ConfigField f2, ConfigField f3)
        {
            if (f1.Matches && f2.Matches && f3.Matches)
            {
                return;
            }

            ThrowConflict(keyKind, key, new[] { f1, f2, f3 });
        }

        public static void ThrowIfConflicting(string keyKind, string key, ConfigField f1, ConfigField f2, ConfigField f3, ConfigField f4)
        {
            if (f1.Matches && f2.Matches && f3.Matches && f4.Matches)
            {
                return;
            }

            ThrowConflict(keyKind, key, new[] { f1, f2, f3, f4 });
        }

        // Lists every field so the message shows the full registered and requested
        // configurations side by side, matching the shape ThrottleStore shipped with.
        private static void ThrowConflict(string keyKind, string key, ConfigField[] fields)
        {
            var sb = new StringBuilder();
            sb.Append(keyKind).Append(" '").Append(key).Append("' already registered with ");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(fields[i].Name).Append('=').Append(fields[i].FormatExisting());
            }
            sb.Append(", but requested ");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(fields[i].Name).Append('=').Append(fields[i].FormatRequested());
            }
            sb.Append(". Configuration changes for existing keys are not supported.");
            throw new InvalidOperationException(sb.ToString());
        }
    }
}
