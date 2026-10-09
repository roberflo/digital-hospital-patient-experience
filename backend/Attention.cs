using System.Text.Json;
namespace Recepcion;

/// <summary>How a hospital wants its reception to attend, as closed choices. Every default is the recommended one, so a hospital
/// that never opens the screen gets the recommended reception. The clinical safeguards are not here: they are not the hospital's to turn off.</summary>
/// <param name="Symptoms">A patient tells their symptoms: «ask» attends and asks whether it is an emergency; «person» offers a person.</param>
/// <param name="NoSlotSoon">Nothing free in the next hours: «ask» whether it is an emergency; «nearest» only offers the next hour.</param>
/// <param name="DoctorChat">A patient asks for a person: also offer the doctors who have a WhatsApp for patients.</param>
/// <param name="Booking">«doctor» asks for the doctor before the hours; «hours» lists the hours of every doctor.</param>
/// <param name="Unregistered">Someone without a record: «register» by WhatsApp; «person» leaves it to reception.</param>
/// <param name="Menu">The options of the welcome menu, out of <see cref="MenuOptions"/>. Null shows them all.</param>
public sealed record Attention(string Symptoms = "ask", string NoSlotSoon = "ask", bool DoctorChat = true, string Booking = "doctor", string Unregistered = "register", string[]? Menu = null)
{
    public static readonly string[] MenuOptions = ["AGENDAR", "MISCITAS", "RECETA", "PERSONA"];
    public static readonly Attention Recommended = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The menu options this hospital shows, in the product's order.</summary>
    public string[] Shown => Menu is { Length: > 0 } ? MenuOptions.Where(Menu.Contains).ToArray() : MenuOptions;

    /// <summary>What is stored for the hospital. Anything unreadable is the recommended reception, never a broken agent.</summary>
    public static Attention Read(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return Recommended;
        try { return JsonSerializer.Deserialize<Attention>(stored, Json)?.Checked() ?? Recommended; }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Recommended; }
    }
    public string Write() => JsonSerializer.Serialize(Checked(), Json);

    public Attention Checked()
    {
        if (Symptoms is not ("ask" or "person") || NoSlotSoon is not ("ask" or "nearest") || Booking is not ("doctor" or "hours") || Unregistered is not ("register" or "person")) throw new ArgumentException("Opción de atención inválida");
        if (Menu is not null && (Menu.Length == 0 || Menu.Any(option => !MenuOptions.Contains(option)))) throw new ArgumentException("El menú necesita al menos una opción válida");
        return this with { Menu = Menu is null || MenuOptions.All(Menu.Contains) ? null : MenuOptions.Where(Menu.Contains).ToArray() };
    }
    // The menu is a set: two records with the same options are the same reception.
    public bool Equals(Attention? other) => other is not null && (Symptoms, NoSlotSoon, DoctorChat, Booking, Unregistered) == (other.Symptoms, other.NoSlotSoon, other.DoctorChat, other.Booking, other.Unregistered) && Shown.SequenceEqual(other.Shown);
    public override int GetHashCode() => HashCode.Combine(Symptoms, NoSlotSoon, DoctorChat, Booking, Unregistered, Shown.Length);
}
