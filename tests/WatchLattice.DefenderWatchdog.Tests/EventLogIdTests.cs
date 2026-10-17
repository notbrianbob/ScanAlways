using System.Reflection;
using WatchLattice.Common.Alerts;
using Xunit;

namespace WatchLattice.DefenderWatchdog.Tests;

public sealed class EventLogIdTests
{
    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(DefenderHealthEvaluator).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(DefenderWatchdog).GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                && f.Name is not nameof(DefenderWatchdog.ModuleId) and not nameof(DefenderHealthEvaluator.BroadExclusionPrefix)))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }
        data.Add(DefenderHealthEvaluator.BroadExclusionPrefix + @"Path:C:\");
        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void EveryAlertCode_HasItsOwnEventLogId(string code)
    {
        Assert.True(AlertEventIds.IsClassified(DefenderWatchdog.ModuleId, code), code);
        Assert.True(AlertEventIds.IsClassified(DefenderWatchdog.ModuleId, code + AlertEventIds.ClearedSuffix), code);
    }
}
