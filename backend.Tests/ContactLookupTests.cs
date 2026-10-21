using Recepcion;
using Xunit;

/// <summary>MR-17: pickers and cards find a contact on the server, not in the first page of 100.</summary>
public sealed class ContactLookupTests : IAsyncLifetime
{
    readonly AgentHarness h = new(), other = new();
    readonly Guid patient = Guid.NewGuid();
    Contact old = null!;
    public async Task InitializeAsync()
    {
        await h.Start();
        // Another hospital's contact, linked to its own patient: never reachable from this one.
        await other.Start(); await other.Link();
        // The one we look for is the oldest: newer contacts fill the first page of 100.
        old = new Contact { TenantId = h.Scope.Id, Name = "Paciente Antigua", Phone = "50371112222", PhoneHash = Guid.NewGuid().ToString(), PatientId = patient, CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) };
        h.Db.Add(old);
        for (var i = 0; i < 100; i++) h.Db.Add(new Contact { TenantId = h.Scope.Id, Name = "Reciente " + i, Phone = "5037000" + i.ToString("0000"), PhoneHash = Guid.NewGuid().ToString() });
        await h.Db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { await h.DisposeAsync(); await other.DisposeAsync(); }

    [Fact]
    public async Task TheFirstPageDoesNotHoldTheOldContact() =>
        Assert.DoesNotContain(await CrmEndpoints.Contacts(h.Db, null, null, null, null, 1), c => c.Id == old.Id);

    [Fact]
    public async Task ByPatientIdOnlyTheLinkedContactComesBack() =>
        Assert.Equal([old.Id], (await CrmEndpoints.Contacts(h.Db, null, patient, null, null, 1)).Select(c => c.Id));

    [Fact]
    public async Task LinkedOnlySkipsTheUnlinkedPage() =>
        Assert.Equal([old.Id], (await CrmEndpoints.Contacts(h.Db, null, null, true, null, 1)).Select(c => c.Id));

    [Fact]
    public async Task ByIdTheCardsGetTheirNames()
    {
        var found = await CrmEndpoints.Contacts(h.Db, null, null, null, [old.Id, h.Contact.Id], 1);
        Assert.Equal(new[] { old.Id, h.Contact.Id }.Order(), found.Select(c => c.Id).Order());
        Assert.Contains(found, c => c.Name == "Paciente Antigua");
    }

    [Fact]
    public async Task SearchStillFindsByName() =>
        Assert.Equal([old.Id], (await CrmEndpoints.Contacts(h.Db, "antigua", null, null, null, 1)).Select(c => c.Id));

    [Fact]
    public async Task AnotherPatientFindsNothing() =>
        Assert.Empty(await CrmEndpoints.Contacts(h.Db, null, Guid.NewGuid(), null, null, 1));

    [Fact]
    public async Task AnotherHospitalsContactIsNotFoundByIdOrPatient()
    {
        Assert.Empty(await CrmEndpoints.Contacts(h.Db, null, null, null, [other.Contact.Id], 1));
        Assert.Empty(await CrmEndpoints.Contacts(h.Db, null, other.Contact.PatientId, null, null, 1));
        Assert.DoesNotContain(await CrmEndpoints.Contacts(h.Db, null, null, true, null, 1), c => c.Id == other.Contact.Id);
    }

    [Fact]
    public async Task MoreThanAPageOfIdsIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => CrmEndpoints.Contacts(h.Db, null, null, null, Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), 1));

    [Theory]
    [InlineData("7111-2222")]
    [InlineData("+503 7111 2222")]
    [InlineData("paciente antigua")]
    [InlineData("PACIENTE ANTÍGUA")]
    // Como escribe recepción: con guiones, espacios, +503, sin tildes o con ellas de más.
    public async Task SearchReadsAPhoneByItsDigitsAndANameWithoutAccents(string q) =>
        Assert.Equal([old.Id], (await CrmEndpoints.Contacts(h.Db, q, null, null, null, 1)).Select(c => c.Id));
}
