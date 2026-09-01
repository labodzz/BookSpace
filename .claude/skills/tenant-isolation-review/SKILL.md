---
name: tenant-isolation-review
description: Checklist provjera tenant izolacije prije mergea, prilagođen BookSpace mehanizmu (ITenantOwned + reflektivni query filter, bez write-guard interceptora). Pokreni ručno prije PR-a za bilo koji feature koji dodaje entitet ili write operaciju.
disable-model-invocation: true
---

# Tenant isolation review (BookSpace)

Manual checklist prije mergea. Kodificira POSTOJEĆI mehanizam iz `BookSpaceDbContext.OnModelCreating` — ne predlaže arhitekturne izmjene, samo provjerava usklađenost sa njim.

## 1. Mehanizam (referenca)

BookSpace NEMA write-guard interceptor. Zaštita ide kroz dva nezavisna sloja:

- **Čitanje**: svaki entitet koji implementira `ITenantOwned` dobija reflektivno primijenjen `HasQueryFilter` u `BookSpaceDbContext.OnModelCreating` — `currentUserContext.TenantId == null || entity.TenantId == currentUserContext.TenantId`. Ovo je automatsko, handler ne mora ništa dodatno pisati za standardan lookup.
- **Pisanje**: handler MORA eksplicitno postaviti `TenantId = currentUserContext.TenantId!.Value` pri kreiranju novog reda (vidi `CreateResourceCommandHandler`). Nema centralnog interceptora koji bi ovo uradio umjesto handlera — izostavljanje ove linije je tiha ranjivost, ne compile-time greška.

Entiteti koji trenutno implementiraju `ITenantOwned` (provjeri da je lista i dalje tačna u `OnModelCreating` prije review-a): `Resource`, `AvailabilityRule`, `BlackoutPeriod`, `Booking` (plus `ResourceApprover` od Grupe 4 nadalje).

Ako feature dodaje novi entitet koji logički pripada tenantu, a nije na listi ni implementira `ITenantOwned`, to je FAIL prije bilo koje druge provjere.

## 2. Query filter checklist

- [ ] Novi upit nad `ITenantOwned` entitetom ide kroz repozitorij metodu bez ručno dodanog `TenantId` filtera preko (globalni filter to već radi — dupli filter nije greška, ali je znak da handler ne vjeruje mehanizmu i vrijedi provjeriti zašto).
- [ ] Ako repo metoda koristi `.IgnoreQueryFilters()`, mora imati komentar koji objašnjava zašto. Poznat, JOŠ NEPOPRAVLJEN slučaj u ovom repou: login lookup trenutno NEMA `.IgnoreQueryFilters()` i zato je podložan bugu gdje stari Authorization header na login zahtjevu pogrešno skopira tenant filter — ovo je otvoren nedostatak, ne prihvaćen obrazac; ne kopiraj to ponašanje u novi kod.

## 3. Write-side checklist

- [ ] Svaki Create handler eksplicitno postavlja `TenantId = currentUserContext.TenantId!.Value` — provjeri tačnu liniju i fajl.
- [ ] Nijedan handler ne prima `TenantId` kao polje iz command/query record-a (klijent ga nikad ne šalje).
- [ ] Update handleri ne dozvoljavaju promjenu `TenantId` (entitet se učita preko repozitorija koji je već tenant-filtriran, pa je reassignment strukturno otežan — provjeri da handler ipak ne postavlja `TenantId` iz request-a u update grani).
- [ ] Roditelj-dijete komande (vidi `add-cqrs-feature` tačka 3) provjeravaju `child.ParentId == request.ParentId`, ne samo `child != null` — sprečava logički pogrešnu operaciju preko mismatched URL-a čak i kad je query filter već tenant-ski ograničio `FindByIdAsync`.

## 4. Test pokrivenost

- [ ] Feature ima barem jedan cross-tenant test. **Napomena**: u ovom repou TRENUTNO ne postoji ustaljena imenska konvencija za takve testove (Resources feature ih još nema — planirano za Group 7 integration testove). Dok se ne ustali drugačije, koristi isti stil kao postojeći handler testovi: `Handle_WithResourceFromAnotherTenant_ThrowsNotFoundException` (unit, mock repo vraća `null` da simulira filtriran rezultat) i, na Api nivou, `{Action}_ForAnotherTenantsResource_ReturnsNotFound` (integration, stvarna baza, dva seed-ovana tenanta).
- [ ] Bez cross-tenant testa za entitet pod filterom = FAIL.

## Format izlaza

Za svaku tačku iz sekcija 2-4: PASS ili FAIL, sa referencom na tačan fajl i liniju u izmijenjenom kodu. Za FAIL: navedi tačno šta nedostaje (ne generički savjet). Ovo je read-only review — ne mijenja kod.
