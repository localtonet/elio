using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>Typing in a list to move to a name. The time is passed in, so nothing here waits.</summary>
public sealed class TypeAheadTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Names = ["apple", "apricot", "Banana", "blueberry", "cherry", "my file.txt"];

    private static DateTime After(int milliseconds) => Start.AddMilliseconds(milliseconds);

    [Fact]
    public void Type_OneLetter_MovesToTheFirstNameThatStartsWithIt()
    {
        var typeAhead = new TypeAhead();

        Assert.Equal(2, typeAhead.Type("b", Names, current: -1, Start));
    }

    [Fact]
    public void Type_InEitherCase_FindsTheName() =>
        Assert.Equal(2, new TypeAhead().Type("B", Names, current: -1, Start));

    [Fact]
    public void Type_MoreLettersQuickly_NarrowTheName()
    {
        var typeAhead = new TypeAhead();
        int current = typeAhead.Type("b", Names, -1, Start);

        Assert.Equal(3, typeAhead.Type("l", Names, current, After(200)));
    }

    [Fact]
    public void Type_LettersAfterAPause_StartOver()
    {
        var typeAhead = new TypeAhead();
        typeAhead.Type("b", Names, -1, Start);

        // Not "bc": that is nothing.
        Assert.Equal(4, typeAhead.Type("c", Names, 2, After(5000)));
        Assert.Equal("c", typeAhead.Typed);
    }

    [Fact]
    public void Type_TheSameLetterAgain_GoesOnToTheNextNameThatHasIt()
    {
        var typeAhead = new TypeAhead();

        int first = typeAhead.Type("a", Names, -1, Start);
        int second = typeAhead.Type("a", Names, first, After(150));
        int third = typeAhead.Type("a", Names, second, After(300));

        Assert.Equal([0, 1, 0], [first, second, third]);
    }

    [Fact]
    public void Type_ALetterAfterAPause_LeavesTheNameChosenForTheNextOne()
    {
        var typeAhead = new TypeAhead();

        Assert.Equal(1, typeAhead.Type("a", Names, current: 0, Start));
        Assert.Equal(0, typeAhead.Type("a", Names, current: 1, After(5000)));
    }

    [Fact]
    public void Type_ALetterNoNameStartsWith_FindsNothing()
    {
        var typeAhead = new TypeAhead();

        Assert.Equal(-1, typeAhead.Type("z", Names, current: 3, Start));
    }

    [Fact]
    public void Type_WhileTheNameChosenStillFits_StaysOnIt()
    {
        var typeAhead = new TypeAhead();

        // "bl" while on "blueberry": the first name that starts with "bl" is the one already there.
        typeAhead.Type("b", Names, 2, Start);
        Assert.Equal(3, typeAhead.Type("l", Names, 3, After(100)));
        Assert.Equal(3, typeAhead.Type("u", Names, 3, After(200)));
    }

    [Fact]
    public void Type_ASpaceInTheMiddleOfAName_IsPartOfIt()
    {
        var typeAhead = new TypeAhead();
        int current = -1;
        int at = 0;
        foreach (string letter in new[] { "m", "y", " ", "f" })
        {
            current = typeAhead.Type(letter, Names, current, After(at += 100));
        }

        Assert.Equal(5, current);
    }

    [Fact]
    public void Type_ASpaceToBeginWith_BelongsToTheList()
    {
        var typeAhead = new TypeAhead();

        Assert.Equal(-1, typeAhead.Type(" ", Names, current: 0, Start));
        Assert.Equal(string.Empty, typeAhead.Typed);
    }

    [Fact]
    public void Type_NothingToChooseFrom_FindsNothing()
    {
        Assert.Equal(-1, new TypeAhead().Type("a", [], current: -1, Start));
    }

    [Fact]
    public void Reset_ForgetsWhatWasTyped()
    {
        var typeAhead = new TypeAhead();
        typeAhead.Type("b", Names, -1, Start);

        typeAhead.Reset();

        Assert.Equal(4, typeAhead.Type("c", Names, 2, After(100)));
    }
}
