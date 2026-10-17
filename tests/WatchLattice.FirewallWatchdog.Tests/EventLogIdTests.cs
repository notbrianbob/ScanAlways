using System.Reflection;
using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.FirewallWatchdog.Tests;

public sealed class EventLogIdTests
{
    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(FirewallHealthEvaluator).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(FirewallWatchdog).GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(FirewallWatchdog.ModuleId)))
        {
            var code = (string)field.GetRawConstantValue()!;
            if (!code.EndsWith('_')) data.Add(code);
            else foreach (var profile in new[] { "DOMAIN", "PRIVATE", "PUBLIC" }) data.Add(code + profile);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryAlertCode_HasItsOwnEventLogId(string code)
    {
        Assert.True(AlertEventIds.IsClassified(FirewallWatchdog.ModuleId, code), code);
        Assert.True(AlertEventIds.IsClassified(FirewallWatchdog.ModuleId, code + AlertEventIds.ClearedSuffix), code);
    }
}
