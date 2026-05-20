# Flusso di sviluppo .NET 8

Questo repository usa una pipeline leggera per mantenere stabile lo sviluppo C#/.NET 8 senza introdurre overhead non necessario. Il progetto principale e' `PCMonitor/PCMonitor.sln`.

## Ciclo di lavoro

1. Aprire un branch piccolo e descrittivo, per esempio `feature/cpu-sampling`.
2. Definire lo scope in un breve appunto: obiettivo, file probabilmente coinvolti, rischi, test attesi.
3. Implementare per incrementi piccoli, mantenendo le modifiche vicine al comportamento richiesto.
4. Eseguire localmente:

```powershell
dotnet restore PCMonitor/PCMonitor.sln
dotnet build PCMonitor/PCMonitor.sln --configuration Release
dotnet test PCMonitor/PCMonitor.sln --configuration Release
```

5. Aprire una pull request con:

- contesto del problema;
- riepilogo delle modifiche;
- comandi di verifica eseguiti;
- note su rischi o parti non coperte da test.

## Pipeline CI

La pipeline e' in `.github/workflows/dotnet-ci.yml` e gira su:

- push verso `main`;
- pull request verso `main`;
- avvio manuale con `workflow_dispatch`.

Gli step sono:

1. checkout del codice;
2. installazione SDK .NET 8;
3. cache dei pacchetti NuGet;
4. `dotnet restore`;
5. `dotnet build` in `Release` con warning trattati come errori;
6. test automatici se esistono progetti con `Test` o `Tests` nel nome;
7. publish dell'eseguibile `PCMonitor`;
8. upload dell'artifact di build.

## Regole di qualita'

`Directory.Build.props` applica a tutti i progetti:

- nullable reference types abilitati;
- code style verificato in build;
- analysis level aggiornato;
- warning trattati come errori.

Se una regola e' troppo severa per un caso specifico, preferire una soppressione locale e motivata invece di abbassare la qualita' globale.

## Uso dei sottoagenti

I sottoagenti servono quando un'attivita' puo' essere divisa in blocchi indipendenti. L'obiettivo e' ridurre il costo in token del ciclo principale: il coordinatore conserva il contesto architetturale, mentre i sottoagenti leggono solo i file necessari al loro incarico.

### Ruoli consigliati

- Coordinatore: mantiene piano, priorita', rischi e integrazione finale.
- Explorer: legge una zona specifica del codice e restituisce solo fatti verificabili, file coinvolti e vincoli.
- Worker: modifica un insieme ristretto di file assegnati.
- Reviewer: controlla regressioni, test mancanti e coerenza con le regole del repository.

### Quando usarli

Usare sottoagenti per:

- analisi indipendenti su moduli diversi;
- implementazioni con write set separati;
- revisione finale in parallelo alla preparazione della PR;
- ricerca di pattern esistenti prima di modificare codice condiviso.

Evitare sottoagenti per:

- fix piccoli in uno o due file;
- decisioni architetturali ancora ambigue;
- modifiche in cui tutti devono toccare gli stessi file;
- task bloccanti per il passo immediatamente successivo.

### Protocollo a basso consumo di token

1. Il coordinatore prepara un brief massimo 10 righe: obiettivo, confini, file concessi, output atteso.
2. Ogni sottoagente riceve solo il contesto indispensabile, non tutta la discussione.
3. Ogni sottoagente restituisce un risultato compatto:

```text
Esito:
File letti:
File modificati:
Decisioni:
Rischi:
Verifica:
```

4. Il coordinatore integra i risultati e risolve conflitti o incoerenze.
5. Un solo reviewer controlla il risultato aggregato.

### Esempio di scomposizione

Per aggiungere monitoraggio CPU, memoria e processi:

- Explorer A: analizza `Program.cs` e propone il punto di ingresso piu' coerente.
- Worker B: crea i servizi di raccolta metriche in una nuova cartella `Monitoring`.
- Worker C: crea test unitari in un progetto `PCMonitor.Tests`.
- Reviewer D: verifica naming, gestione errori, test e impatto sulla pipeline.

I worker B e C possono lavorare in parallelo solo se concordano prima i contratti pubblici. Se il contratto non e' stabile, il coordinatore lo definisce localmente prima di delegare.

## Checklist PR

- La soluzione compila in `Release`.
- I test passano o e' documentato perche' non esistono test.
- I warning non sono ignorati.
- La PR contiene modifiche coerenti con lo scope dichiarato.
- I sottoagenti, se usati, hanno lavorato su confini chiari e non sovrapposti.
