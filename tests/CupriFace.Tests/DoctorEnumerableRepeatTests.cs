using CupriFace.Diagnostics;
using Xunit;

namespace CupriFace.Tests;

/// <summary>A repeat over a property typed <c>IEnumerable&lt;T&gt;</c> itself — not a List or an array.
/// The doctor's item-type lookup asked the interface for its interfaces, which do not include
/// itself, and reported every binding in the repeat as a typo (found by the build-time check's
/// author, who handled the case in the analyzer).</summary>
public class DoctorEnumerableRepeatTests
{
    public sealed class Row { public string Title { get; set; } = "row-title"; }
    public sealed class ListModel { public IEnumerable<Row> Items { get; set; } = [new Row()]; }

    [Fact]
    public void A_repeat_over_an_IEnumerable_property_finds_its_item_type()
    {
        var report = CupriDoctor.Check("<body><div data-repeat='Items'>{{Title}}</div></body>", "", model: new ListModel());
        Assert.DoesNotContain(report.Findings, f => f.Code == "CF0060");
    }

    [Fact]
    public void A_real_typo_inside_it_is_still_reported()
    {
        var report = CupriDoctor.Check("<body><div data-repeat='Items'>{{Titel}}</div></body>", "", model: new ListModel());
        Assert.Contains(report.Findings, f => f.Code == "CF0060" && f.Fix.Contains("{{Title}}"));
    }
}
