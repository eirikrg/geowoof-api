using GeoWoof.Modules.Pets.Application;
using GeoWoof.Modules.Pets.Domain;
using GeoWoof.Modules.Pets.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace GeoWoof.Modules.Pets.UnitTests;

public sealed class PetDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var context = new PetsDbContextDesignTimeFactory().CreateDbContext([]);

        context.Database.HasPendingModelChanges().ShouldBeFalse("Run `dotnet ef migrations add` for the Pets module.");
    }

    [Fact]
    public void Only_the_owner_can_see_the_microchip()
    {
        var owner = Guid.CreateVersion7();
        var pet = Pet.Create(Guid.CreateVersion7(), owner, Details(microchip: "941000024567890"), null, Now);

        pet.MicrochipVisibleTo(owner).ShouldBe("941000024567890");
        pet.MicrochipVisibleTo(Guid.CreateVersion7()).ShouldBeNull();
    }

    [Fact]
    public void Text_is_normalised_and_empty_values_are_cleared()
    {
        var pet = Pet.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Details(name: "  Luna ", breed: "   ", microchip: " 123 "), null, Now);

        pet.Name.ShouldBe("Luna");
        pet.Breed.ShouldBeNull();
        pet.Microchip.ShouldBe("123");
    }

    [Fact]
    public void Changing_the_species_drops_the_free_text_one()
    {
        var pet = Pet.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Details() with { Species = PetSpecies.Other, SpeciesOther = "Hurón" }, null, Now);
        pet.SpeciesOther.ShouldBe("Hurón");

        pet.Update(Details() with { Species = PetSpecies.Cat, SpeciesOther = "Hurón" }, null, Now.AddDays(1));

        pet.SpeciesOther.ShouldBeNull();
        pet.UpdatedAt.ShouldBe(Now.AddDays(1));
        pet.CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public void Each_change_raises_its_event()
    {
        var pet = Pet.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Details(), null, Now);
        pet.Update(Details(name: "Lunita"), null, Now);
        pet.Delete();

        pet.DomainEvents.Select(domainEvent => domainEvent.GetType()).ShouldBe(
            [typeof(PetCreatedDomainEvent), typeof(PetUpdatedDomainEvent), typeof(PetDeletedDomainEvent)]);
    }

    [Theory]
    [InlineData("0.01", true)]
    [InlineData("18.4", true)]
    [InlineData("200", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("200.01", false)]
    [InlineData("18.456", false)]
    public void Weights_must_be_realistic(string weight, bool expected)
    {
        PetWeightEntry.IsValidWeight(decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
    }

    [Fact]
    public void A_wrong_weight_never_reaches_the_entity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => PetWeightEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), new DateOnly(2026, 1, 1), 0));
    }

    [Theory]
    [InlineData("Dog", null, true)]
    [InlineData("Other", "Hurón", true)]
    [InlineData("Other", null, false)]
    [InlineData("Other", "   ", false)]
    [InlineData("Dog", "Hurón", false)]
    [InlineData("dog", null, false)]
    [InlineData("Dragon", null, false)]
    public void The_free_text_species_goes_only_with_other(string species, string? speciesOther, bool expected)
    {
        Validate(Input() with { Species = species, SpeciesOther = speciesOther }).ShouldBe(expected);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("L", true)]
    public void A_pet_needs_a_name(string name, bool expected)
    {
        Validate(Input() with { Name = name }).ShouldBe(expected);
        Validate(Input() with { Name = new string('x', Pet.NameMaxLength + 1) }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("941000024567890", true)]
    [InlineData("ABC123", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("941-000", false)]
    [InlineData("con espacios", false)]
    public void Microchips_only_have_letters_and_digits(string? microchip, bool expected)
    {
        Validate(Input() with { Microchip = microchip }).ShouldBe(expected);
        Validate(Input() with { Microchip = new string('1', Pet.MicrochipMaxLength + 1) }).ShouldBeFalse();
    }

    [Fact]
    public void Dates_cannot_be_impossible()
    {
        Validate(Input() with { BirthDate = new DateOnly(2021, 4, 12) }).ShouldBeTrue();
        Validate(Input() with { BirthDate = null }).ShouldBeTrue();
        Validate(Input() with { BirthDate = new DateOnly(1989, 12, 31) }).ShouldBeFalse();

        // Today in a time zone ahead of UTC is still valid; the day after is not.
        Validate(Input() with { BirthDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1) }).ShouldBeTrue();
        Validate(Input() with { BirthDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(2) }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Female", "Medium", true)]
    [InlineData("Unknown", null, true)]
    [InlineData("Other", null, false)]
    [InlineData("Male", "Huge", false)]
    public void Sex_and_size_use_known_values(string sex, string? size, bool expected)
    {
        Validate(Input() with { Sex = sex, Size = size }).ShouldBe(expected);
    }

    private static bool Validate(PetInput input) => new PetInputValidator(new FakeTimeProvider(Now)).Validate(input).IsValid;

    private static PetInput Input() => new("Luna", "Dog", null, null, null, "Female", null, null, false, null, null, null);

    private static PetDetails Details(string name = "Luna", string? breed = null, string? microchip = null) =>
        new(name, PetSpecies.Dog, null, breed, null, PetSex.Female, null, null, false, microchip, null);
}
