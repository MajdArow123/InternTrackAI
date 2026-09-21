using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// Parsing and bounding for the field-awareness values. Everything here takes input that may come from
/// a stale form post or (from Phase 2) a model's JSON, so the contract is "never throw, never guess":
/// an unrecognised category becomes <see cref="FieldCategory.Other"/> because something was clearly
/// meant, an unrecognised level becomes null because guessing one would change how every prompt pitches
/// its answer.
/// </summary>
public class ProfileFieldsTests
{
    [Theory]
    [InlineData("Healthcare", FieldCategory.Healthcare)]
    [InlineData("healthcare", FieldCategory.Healthcare)]
    [InlineData("  TRADES  ", FieldCategory.Trades)]
    [InlineData("PublicSector", FieldCategory.PublicSector)]
    public void Known_categories_parse_regardless_of_casing_or_padding(string input, FieldCategory expected) =>
        Assert.Equal(expected, ProfileFields.ParseCategory(input));

    [Theory]
    [InlineData("Astrology")]
    [InlineData("Software Engineering")]   // a Field value posted into the category slot
    [InlineData("99")]
    [InlineData("{}")]
    public void An_unrecognised_category_falls_back_to_Other(string input) =>
        Assert.Equal(FieldCategory.Other, ProfileFields.ParseCategory(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_category_is_not_set_rather_than_Other(string? input) =>
        Assert.Null(ProfileFields.ParseCategory(input));

    [Fact]
    public void A_numeric_category_string_is_not_read_as_an_enum_value()
    {
        // Enum.TryParse accepts "5" as Healthcare. A number arriving where a name belongs is a broken
        // client, not a choice, so it must not silently become whichever member happens to sit there.
        Assert.Equal(FieldCategory.Other, ProfileFields.ParseCategory("5"));
        Assert.Equal(FieldCategory.Other, ProfileFields.ParseCategory("0"));
    }

    [Theory]
    [InlineData("Student", SeniorityLevel.Student)]
    [InlineData("entrylevel", SeniorityLevel.EntryLevel)]
    [InlineData(" Senior ", SeniorityLevel.Senior)]
    public void Known_levels_parse_regardless_of_casing(string input, SeniorityLevel expected) =>
        Assert.Equal(expected, ProfileFields.ParseSeniority(input));

    [Theory]
    [InlineData("Principal")]
    [InlineData("Staff")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("3")]
    public void An_unrecognised_level_stays_unset(string? input) =>
        Assert.Null(ProfileFields.ParseSeniority(input));

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(-7, ProfileFields.MinYearsExperience)]
    [InlineData(9999, ProfileFields.MaxYearsExperience)]
    public void Years_are_clamped_not_rejected(int? input, int? expected) =>
        Assert.Equal(expected, ProfileFields.Years(input));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Registered Nursing  ", "Registered Nursing")]
    public void Text_trims_and_turns_blank_into_null(string? input, string? expected) =>
        Assert.Equal(expected, ProfileFields.Text(input));

    [Fact]
    public void Text_caps_a_paste_at_the_stored_length()
    {
        var result = ProfileFields.Text(new string('y', ProfileFields.MaxTextLength + 200));

        Assert.Equal(ProfileFields.MaxTextLength, result!.Length);
    }

    [Fact]
    public void Two_word_enum_members_get_a_readable_label()
    {
        Assert.Equal("Public sector", ProfileFields.Display(FieldCategory.PublicSector));
        Assert.Equal("Entry level", ProfileFields.Display(SeniorityLevel.EntryLevel));
        Assert.Equal("Healthcare", ProfileFields.Display(FieldCategory.Healthcare));
        Assert.Equal("Student", ProfileFields.Display(SeniorityLevel.Student));
    }

    [Fact]
    public void Every_enum_member_has_a_label_and_none_of_them_is_blank()
    {
        Assert.All(Enum.GetValues<FieldCategory>(), c => Assert.False(string.IsNullOrWhiteSpace(ProfileFields.Display(c))));
        Assert.All(Enum.GetValues<SeniorityLevel>(), s => Assert.False(string.IsNullOrWhiteSpace(ProfileFields.Display(s))));
    }

    [Theory]
    [InlineData("Registered Nursing", FieldCategory.Healthcare, "Registered Nursing (Healthcare)")]
    [InlineData("Registered Nursing", null, "Registered Nursing")]
    [InlineData(null, FieldCategory.Healthcare, null)]
    [InlineData("  ", FieldCategory.Healthcare, null)]
    public void Label_pairs_the_free_text_with_its_category_when_there_is_one(string? field, FieldCategory? category, string? expected) =>
        Assert.Equal(expected, ProfileFields.Label(field, category));

    /// <summary>
    /// Both enums are stored as ints (CLAUDE.md §7) with values written out explicitly so a member can
    /// be appended without shifting rows already in the database. This fails if someone renumbers one.
    /// </summary>
    [Fact]
    public void The_stored_enum_values_are_pinned_so_appending_a_member_can_never_shift_them()
    {
        Assert.Equal(1, (int)FieldCategory.Technology);
        Assert.Equal(2, (int)FieldCategory.Engineering);
        Assert.Equal(3, (int)FieldCategory.Business);
        Assert.Equal(4, (int)FieldCategory.Finance);
        Assert.Equal(5, (int)FieldCategory.Healthcare);
        Assert.Equal(6, (int)FieldCategory.Education);
        Assert.Equal(7, (int)FieldCategory.Design);
        Assert.Equal(8, (int)FieldCategory.Science);
        Assert.Equal(9, (int)FieldCategory.Legal);
        Assert.Equal(10, (int)FieldCategory.Trades);
        Assert.Equal(11, (int)FieldCategory.Media);
        Assert.Equal(12, (int)FieldCategory.PublicSector);
        Assert.Equal(13, (int)FieldCategory.Other);

        Assert.Equal(1, (int)SeniorityLevel.Student);
        Assert.Equal(2, (int)SeniorityLevel.EntryLevel);
        Assert.Equal(3, (int)SeniorityLevel.Junior);
        Assert.Equal(4, (int)SeniorityLevel.Mid);
        Assert.Equal(5, (int)SeniorityLevel.Senior);

        // No member may be 0: an int column defaulting to 0 would read back as a real member.
        Assert.DoesNotContain(0, Enum.GetValues<FieldCategory>().Select(v => (int)v));
        Assert.DoesNotContain(0, Enum.GetValues<SeniorityLevel>().Select(v => (int)v));
    }
}
