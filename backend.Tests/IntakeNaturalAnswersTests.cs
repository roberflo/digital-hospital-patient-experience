using Recepcion;
using Xunit;

/// <summary>The registration form reads what people actually write: the whole name at once, a birth date in words.
/// It says back what it understood, so the patient confirms it. Synthetic people only.</summary>
public sealed class IntakeNaturalAnswersTests
{
    static readonly DateOnly Today = new(2026, 10, 4);

    [Theory]
    [InlineData("Roberto Flores Sintético")]
    [InlineData("Me llamo Roberto Flores Sintético")]
    [InlineData("mi nombre es Roberto Flores Sintético")]
    public void AWholeNameIsSaidBackAndOnlyTheSurnamesAreAsked(string answer)
    {
        var (state, prompt, choices) = Intake.Start().State.Answer(answer, Today);
        Assert.Equal(2, state.Step);
        Assert.Contains("Entonces tu nombre completo es *Roberto Flores Sintético*", prompt);
        // Which words are surnames cannot be guessed («Juan Carlos Pérez»): the patient picks.
        Assert.Equal(["Flores Sintético", "Sintético"], choices!.Options.Select(option => option.Title));

        var (named, _, _) = state.Answer("Flores Sintético", Today);
        Assert.Equal("Roberto", named.GivenNames);
        Assert.Equal("Flores Sintético", named.FamilyNames);
        Assert.Equal(3, named.Step);
    }

    [Fact]
    public void SurnamesTooLongForAButtonAreTypedNotTapped()
    {
        var (state, prompt, choices) = Intake.Start().State.Answer("Ana Montenegro-Sintético Villavicencio", Today);
        Assert.Null(choices);
        Assert.Contains("¿Cuáles son tus apellidos?", prompt);
        var (named, _, _) = state.Answer("Montenegro-Sintético Villavicencio", Today);
        Assert.Equal("Ana", named.GivenNames);
    }

    [Fact]
    public void TwoGivenNamesStayTwoGivenNames()
    {
        var (state, _, _) = Intake.Start().State.Answer("Ana María", Today);
        var (named, _, _) = state.Answer("López Prueba", Today);
        Assert.Equal("Ana María", named.GivenNames);
        Assert.Equal("López Prueba", named.FamilyNames);
    }

    [Fact]
    public void ASurnameAlreadyWrittenWithTheNameIsNotKeptTwice()
    {
        var (state, _, _) = Intake.Start().State.Answer("Roberto Flores", Today);
        var (named, _, _) = state.Answer("flores", Today);
        Assert.Equal("Roberto", named.GivenNames);
        Assert.Equal("flores", named.FamilyNames);
    }

    [Theory]
    [InlineData("15 de enero de 1985")]
    [InlineData("nací el 15 de enero de 1985")]
    [InlineData("el 15 de enero del 1985")]
    [InlineData("15 enero 1985")]
    [InlineData("enero 15, 1985")]
    [InlineData("Mi fecha es 15/01/1985")]
    [InlineData("15/01/1985")]
    public void ABirthDateInWordsIsReadAndSaidBack(string answer)
    {
        var (state, prompt, _) = new Intake(3, "Roberto", "Flores Sintético").Answer(answer, Today);
        Assert.Equal("1985-01-15", state.BirthDate);
        Assert.StartsWith("Entonces tu fecha de nacimiento es el *15 de enero de 1985*.", prompt);
    }

    [Theory]
    [InlineData("no me acuerdo")]
    [InlineData("31 de febrero de 1985")]
    [InlineData("15/01/85")] // two digits of year could be 1985 or 2085: asked again, never guessed
    public void WhatIsNotADateIsAskedAgain(string answer)
    {
        var (state, prompt, _) = new Intake(3, "Roberto", "Flores Sintético").Answer(answer, Today);
        Assert.Null(state.BirthDate);
        Assert.StartsWith("No pude leer esa fecha.", prompt);
    }
}
