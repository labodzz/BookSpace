---
name: add-cqrs-feature
description: Scaffolduje novu command/query (CQRS) za BookSpace po postojećem obrascu iz Resources feature-a — file-per-request, ručno pisan mediator, repozitorij-po-entitetu, ErrorCodes. Koristi kad se dodaje novi endpoint u Resources/Availability/Booking/Approval modulu.
---

# Dodavanje novog CQRS feature-a (BookSpace)

Kodificira POSTOJEĆI obrazac iz `BookSpace.Application/Resources/` (Resource/AvailabilityRule/BlackoutPeriod CRUD). Ne uvodi novu arhitekturu — samo prati šta ti primjeri već rade.

## 1. Jedan fajl po request-u, grupisano po VERB-u

Unutar `BookSpace.Application/{Feature}/` postoji po jedan podfolder za svaki CRUD glagol — `Create/`, `Update/`, `Delete/`, `Get/` — i SVI entiteti tog feature-a dijele isti glagol-folder. Npr. `Resources/Create/` sadrži `CreateResourceCommandRequest.cs`, `CreateAvailabilityRuleCommandRequest.cs` i `CreateBlackoutPeriodCommandRequest.cs` jedno pored drugog — ne postoji `CreateResource/` folder odvojen od `CreateAvailabilityRule/` foldera. Ovo je namjerno: cilj je da se sve "Create" operacije feature-a nađu na jednom mjestu bez obzira na entitet, ne da se svaki use-case izoluje u svoj mikro-folder. Repozitorij interfejsi (`I{Entity}Repository.cs`) i dijeljeni helperi (npr. `TimeZoneValidation.cs`) ostaju u korijenu `{Feature}/` foldera, van glagol-podfoldera, jer nisu vezani za jednu komandu/upit. Namespace ostaje flat (`BookSpace.Application.Resources`) bez obzira na fizički podfolder — razdvajanje je čisto fizičko/organizaciono, ne mijenja se `using` u drugim fajlovima. Isti raspored (`Create/`, `Update/`, `Delete/`, `Get/`) se ponavlja identično u `BookSpace.Application.Tests/{Feature}/`.

Svaki use-case i dalje ide u JEDAN fajl unutar svog glagol-foldera: record, validator (ako ima šta validirati) i handler u ISTOM fajlu, ne razdvojeno po sloju.

Sam `IRequest<T>` record nosi sufiks **Request** nakon Command/Query (`CreateResourceCommandRequest`, `GetResourcesQueryRequest`) — eksplicitno govori da je ovo ulazni objekat, ne response i ne handler, čim ga neko vidi u kodu. Validator prati isto (`CreateResourceCommandRequestValidator`). Handler NE dobija "Request" — ostaje `{UseCase}CommandHandler`/`{UseCase}QueryHandler`, jer handler nije "the request", on ga obrađuje.

```csharp
namespace BookSpace.Application.Resources;

public sealed record CreateResourceCommandRequest(
    Guid ResourceTypeId,
    string Name,
    string? Description,
    int Capacity,
    bool RequiresApproval,
    string TimeZoneId) : IRequest<CreateResourceResponse>;

public sealed record CreateResourceResponse(
    Guid Id, Guid ResourceTypeId, string Name, string? Description,
    int Capacity, bool RequiresApproval, ResourceStatus Status, string TimeZoneId);

public sealed class CreateResourceCommandRequestValidator : AbstractValidator<CreateResourceCommandRequest>
{
    public CreateResourceCommandRequestValidator()
    {
        RuleFor(command => command.ResourceTypeId).NotEmpty();
        RuleFor(command => command.Name).NotEmpty().MaximumLength(200);
        // ... format/prisustvo, ništa business
    }
}

public sealed class CreateResourceCommandHandler(IResourceRepository resourceRepository, ICurrentUserContext currentUserContext)
    : IRequestHandler<CreateResourceCommandRequest, CreateResourceResponse>
{
    public async Task<CreateResourceResponse> Handle(CreateResourceCommandRequest request, CancellationToken cancellationToken)
    {
        // ...
        return new CreateResourceResponse(resource.Id, resource.ResourceTypeId, resource.Name,
            resource.Description, resource.Capacity, resource.RequiresApproval, resource.Status, resource.TimeZoneId);
    }
}
```

Referenca: `CreateResourceCommandRequest.cs`, `GetResourcesQueryRequest.cs`, `DeleteBlackoutPeriodCommandRequest.cs`.

**Svaki command/query ima svoj SOPSTVENI response record** — `CreateResourceResponse`, `UpdateResourceResponse`, `DeleteResourceResponse`, `GetResourceResponse` i `GetResourcesResponseItem` (item unutar `PagedResult<>`) su četiri odvojena, strukturno slična, ali NEZAVISNA record-a, svaki definisan u fajlu use-case-a koji ga vraća — ne jedan dijeljeni `{Entity}Response.cs`. Namjerno: svaka komanda tako nezavisno vlada svojim contract-om (Create sutra može dobiti polje koje Get nikad neće imati, bez da to nekog drugog handlera dotiče), i lakše je pratiti koji response pripada kojoj komandi. Cijena je ponavljanje polja preko više record-a — prihvaćeno svjesno, ne previd.

Nema dijeljene mapping ekstenzije (`ToResponse()`) — svaki handler ručno konstruiše svoj `new XResponse(...)` na kraju, direktno od entiteta. Naming: `{UseCase}Response` za pojedinačan rezultat, `{UseCase}ResponseItem` za item unutar liste/`PagedResult<>` (vidi `GetResourcesResponseItem`, `GetAvailabilityRulesResponseItem`). Izuzetak: čiste delete komande i dalje vraćaju `Unit`, ne dobijaju response record (tačka 3).

## 2. Validator

Validira ISKLJUČIVO oblik/prisustvo polja (`NotEmpty`, `MaximumLength`, `GreaterThan`, `InclusiveBetween`, `Must(...)` za format kao vremenska zona). NIKAD ne provjerava postojanje u bazi, tenant, ni business pravila — to ide u handler. Izvršava se automatski kroz `ValidationBehavior` pipeline prije handlera — ne poziva se ručno.

Query-ji sa paginacijom (`Page`, `PageSize`) uvijek validiraju `Page >= 1` i `PageSize` preko `InclusiveBetween(1, 100)` — vidi `GetResourcesQueryRequest.cs`.

Pure-by-id komande (npr. `DeleteResourceCommandRequest(Guid Id)`) nemaju validator — nepostojeći Guid se rješava u handleru kao `NotFoundException`.

## 3. Handler

- DI kroz primary constructor: repozitorij(i) + eventualno `ICurrentUserContext`.
- Na create: `TenantId = currentUserContext.TenantId!.Value` — NIKAD iz request-a klijenta.
- Not-found lookup: `?? throw new NotFoundException($"...", ErrorCodes.X)`.
- Duplicate/conflict provjera: `if (await repo.ExistsXAsync(...)) throw new ConflictException($"...", ErrorCodes.Y)` — ovo je "friendly" pre-check; prava garancija je unique index u bazi. `DbContextConcurrencyExtensions.SaveChangesHandlingConflictsAsync` hvata `SqlException` sa brojem 2601/2627 i prevodi je u `ConflictException` ako pre-check izgubi race — zato repo `SaveChangesAsync` mora zvati baš tu ekstenziju (tačka 5), ne goli `dbContext.SaveChangesAsync`.
- **Roditelj-dijete mismatch guard**: kad komanda prima i `ParentId` i `ChildId` (npr. `DeleteBlackoutPeriodCommandRequest(ResourceId, BlackoutId)`), provjera mora biti `if (child is null || child.ParentId != request.ParentId) throw new NotFoundException(...)` — ne samo `child is null`. Bez ovoga, mismatched URL (dijete tuđeg roditelja u putanji) tiho "uspije". Vidi `DeleteBlackoutPeriodCommandRequest.cs`.
- Soft-delete umjesto hard delete kad FK ima `OnDelete(Restrict)`: postavi `Status = Archived` umjesto brisanja reda, i učini operaciju idempotentnom (ako je već arhiviran, samo vrati stanje bez ponovnog snimanja). Vidi `DeleteResourceCommandRequest.cs`.
- Komande bez smislenog povratnog tipa (čisti delete) vraćaju `Unit` (`IRequest<Unit>`), ne `void` i ne `bool`.

## 4. ErrorCodes

Novi throw-ovi (za entitete uvedene od Grupe 3 — BlackoutPeriod — nadalje) dodaju konstantu u `BookSpace.Application/Common/ErrorCodes.cs` (`internal static class`, dotted-notation string kao `"BlackoutPeriod.NotFound"`) i proslijede je kao drugi, opcioni argument `NotFoundException`/`ConflictException`. Handleri napisani PRIJE Grupe 3 (Resource, AvailabilityRule) namjerno ostaju bez ErrorCode-a — ne mijenjaj ih retroaktivno bez eksplicitnog dogovora.

## 5. Repozitorij

- Interfejs: `BookSpace.Application/{Feature}/I{Entity}Repository.cs` — `Task<T?>` za pojedinačni lookup, `Task<IReadOnlyList<T>>` ili `Task<PagedResult<T>>` za kolekcije, `CancellationToken` zadnji parametar, **nikad** `TenantId` parametar (globalni query filter to već radi).
- Implementacija: `internal sealed class {Entity}Repository(BookSpaceDbContext dbContext) : I{Entity}Repository` u `BookSpace.Infrastructure/Persistence/`.
- Mutating metode (`AddAsync`, `RemoveAsync`, in-place izmjena) NE zovu `SaveChangesAsync` same — repo izlaže sopstveni `SaveChangesAsync(CancellationToken)` koji interno zove `dbContext.SaveChangesHandlingConflictsAsync(cancellationToken)`.
- Jedan feature (npr. Resources) obično ima VIŠE uskih repozitorija, jedan po entitetu (`IResourceRepository`, `IAvailabilityRuleRepository`, `IBlackoutPeriodRepository`) — ne jedan fat repo za cijeli modul.
- Registracija u `BookSpace.Infrastructure/DependencyInjection.cs` je EKSPLICITNA (`services.AddScoped<IX, X>()`), nema assembly scan-a za repozitorije.

## 6. DI za handler/validator

Handler i validator se NE registruju ručno — auto-registrovani su assembly scan-om u `BookSpace.Application/DependencyInjection.cs`. Ručno registruj SAMO: novi repozitorij (Infrastructure DI, tačka 5) i novi `IExceptionHandler` ako uvodiš novi tip exception-a (`Program.cs`).

## 7. Završetak

Feature se ne smatra gotovim dok testovi nisu napisani — pozovi `write-handler-tests` odmah nakon handlera/validatora, u istom PR-u. Ako feature dira novi entitet koji je `ITenantOwned`, pokreni i `tenant-isolation-review` prije PR-a.
