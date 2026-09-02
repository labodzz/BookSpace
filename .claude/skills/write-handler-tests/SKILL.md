---
name: write-handler-tests
description: Piše unit testove (handler + validator) za novi CQRS feature po postojećoj Moq konvenciji iz BookSpace.Application.Tests/Resources. Koristi odmah nakon add-cqrs-feature, u istom PR-u kao i feature.
---

# Pisanje testova za CQRS handler/validator (BookSpace)

Kodificira POSTOJEĆI obrazac iz `BookSpace.Application.Tests/Resources/` (npr. `UpdateBlackoutPeriodCommandHandlerTests.cs`, `CreateBlackoutPeriodCommandRequestValidatorTests.cs`). Ne uvodi novi test pattern.

## 1. Framework i lokacija

xUnit (`[Fact]`) + Moq. Lokacija: `BookSpace.Application.Tests/{Feature}/{Verb}/` — isti raspored kao produkcijski kod: podfolder po CRUD glagolu (`Create/`, `Update/`, `Delete/`, `Get/`), entiteti feature-a dijele isti glagol-folder (npr. `Resources/Create/` sadrži testove i za Resource i za AvailabilityRule i za BlackoutPeriod create).

## 2. Handler testovi

- Klasa i fajl: `public sealed class {UseCase}HandlerTests` u `{UseCase}HandlerTests.cs` — testira `{UseCase}Handler`, NE dobija "Request" u imenu (handler ga ni sam nema).
- `Mock<T>` polje po zavisnosti handlera (`private readonly Mock<IBlackoutPeriodRepository> _blackoutPeriodRepository = new();`), NE jedan veliki container mock.
- Privatna `CreateSut()` fabrika: `private {UseCase}Handler CreateSut() => new(_repo.Object, ...);`
- Instanca ulaznog objekta u testu je tipa `{UseCase}CommandRequest`/`{UseCase}QueryRequest` (npr. `new CreateBlackoutPeriodCommandRequest(...)`), lokalna varijabla se zove `request`, ne `command`.
- Naming metode: `Handle_With{Scenario}_{Outcome}` — npr. `Handle_WithMatchingPeriod_RemovesItAndSaves`, `Handle_WithUnknownPeriod_ThrowsNotFoundException`, `Handle_WithPeriodBelongingToADifferentResource_ThrowsNotFoundException`.
- Za svaki handler sa roditelj-dijete mismatch guard-om (vidi `add-cqrs-feature` tačka 3), OBAVEZNO postoji treći test scenario za taj mismatch, odvojen od "unknown id" scenarija — obje grane bacaju istu exception ali iz različitih uslova, obje moraju biti pokrivene.
- Success test provjerava i povratnu vrijednost (`Assert.Equal(newStart, result.StartUtc)`) I da je repo pozvan (`_repo.Verify(r => r.SaveChangesAsync(...), Times.Once)`).
- Failure test (mismatch/not-found) provjerava da `SaveChangesAsync` NIJE pozvan (`Times.Never`) — dokazuje da handler ne piše ništa prije provjere.
- Ako exception ima `ErrorCode`, provjeri ga eksplicitno: `Assert.Equal("BlackoutPeriod.NotFound", exception.ErrorCode);` — samo za throw-ove uvedene od Grupe 3 nadalje (vidi `add-cqrs-feature` tačka 4); stariji throw-ovi (Resource/AvailabilityRule) nemaju ErrorCode i ne treba ga testirati.

## 3. Validator testovi

- Klasa i fajl: `public sealed class {UseCase}CommandRequestValidatorTests` (ili `QueryRequestValidatorTests`) u `{UseCase}CommandRequestValidatorTests.cs` — ime prati puni naziv validatora pod testom (`{UseCase}CommandRequestValidator`), ne skraćuje se.
- `private readonly {UseCase}CommandRequestValidator _sut = new();` — direktna instanca, bez DI, bez mocka.
- Naming metode: `Validate_With{Scenario}_HasNoErrors` (happy path) / `Validate_With{Scenario}_HasErrors` (po jedan test za svako pravilo — prazan Guid, end nije poslije start-a, prazan tekst...).
- `Assert.True(result.IsValid)` / `Assert.False(result.IsValid)` — ne provjeravaj tačan tekst poruke, samo validnost.
- Preskoči ovaj fajl za pure-by-id komande bez validatora (npr. `DeleteResourceCommandRequest`).

## 4. Šta NE testirati ovdje

- Ne testiraj `IExceptionHandler`/HTTP status kod na ovom nivou — to je pokriveno zasebno u `BookSpace.Api.Tests/ErrorHandling/`.
- Ne testiraj tenant filter ovdje (mock repozitorija ionako ne zna za tenant) — to ide kroz `tenant-isolation-review` i integration testove u `BookSpace.Api.Tests`.
