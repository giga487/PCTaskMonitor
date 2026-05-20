# PCMonitor

PCMonitor e' un monitor .NET 8 per Windows che campiona periodicamente processi e interfacce di rete, scrivendo gli eventi nei log tramite Serilog.

## Avvio

```powershell
dotnet restore PCMonitor/PCMonitor.sln
dotnet run --project PCMonitor/PCMonitor.csproj
```

Di default il servizio HTTP ascolta su `http://0.0.0.0:5000` e i log sono esposti da `/logs`.

## Configurazione

La configurazione principale si trova in `PCMonitor/appsettings.json`. Il file viene ricaricato a runtime quando cambia, quindi molte impostazioni di monitoraggio possono essere aggiornate senza riavviare il processo.

### Monitoring

La sezione `Monitoring` controlla il ciclo di campionamento:

```json
"Monitoring": {
  "SampleIntervalSeconds": 5,
  "ProcessCpuThresholdPercent": 15.0,
  "TotalCpuThresholdPercent": 80,
  "TopCpuProcessesCount": 5
}
```

- `SampleIntervalSeconds`: ogni quanti secondi campionare processi e rete. Il minimo effettivo e' 1 secondo.
- `ProcessCpuThresholdPercent`: soglia CPU per singolo processo; se superata viene scritto un warning.
- `TotalCpuThresholdPercent`: soglia CPU totale stimata; se superata vengono loggati i processi piu' pesanti.
- `TopCpuProcessesCount`: quanti processi includere nel riepilogo quando la CPU totale supera soglia.

### TrackedTasks

`Monitoring:TrackedTasks` serve a tracciare a ogni campionamento una lista esplicita di processi divisa per nome.

```json
"TrackedTasks": {
  "Enabled": true,
  "Names": [
    "notepad",
    "PCMonitor"
  ]
}
```

Quando e' abilitato, per ogni nome configurato viene scritta una riga di log con:

- `TaskName`: nome del processo monitorato;
- `RunningCount`: quante istanze sono in esecuzione;
- `TotalCpuPercent`: CPU totale delle istanze trovate;
- `Pids`: PID delle istanze;
- `SampleTime`: timestamp del campionamento.

I nomi possono essere indicati con o senza `.exe`; il confronto non distingue maiuscole/minuscole. La CPU e' significativa dal secondo campionamento in poi, perche' viene calcolata confrontando il campione corrente con quello precedente.

### Network

La sezione `Monitoring:Network` controlla il monitoraggio delle interfacce di rete:

```json
"Network": {
  "Enabled": true,
  "InterfaceNameContains": [],
  "ReceivedBytesPerSecondThreshold": 10485760,
  "SentBytesPerSecondThreshold": 10485760,
  "TotalBytesPerSecondThreshold": 20971520
}
```

- `Enabled`: abilita o disabilita il monitoraggio rete.
- `InterfaceNameContains`: se vuoto monitora tutte le interfacce attive non loopback; altrimenti monitora solo quelle il cui nome o descrizione contiene uno dei valori indicati.
- `ReceivedBytesPerSecondThreshold`: soglia byte/s in ricezione.
- `SentBytesPerSecondThreshold`: soglia byte/s in invio.
- `TotalBytesPerSecondThreshold`: soglia byte/s totale.

### AppLogging

PCMonitor usa Serilog. La configurazione custom del logger si trova in `AppLogging`, non in `Logging`, per evitare conflitti con la sezione standard di ASP.NET.

```json
"AppLogging": {
  "MinimumLevel": "Information",
  "FilePath": "logs/pcmonitor-.log",
  "RollingInterval": "Day",
  "RetainedDays": 30,
  "FileSizeLimitBytes": 10485760
}
```

- `MinimumLevel`: livello minimo Serilog, per esempio `Debug`, `Information`, `Warning`, `Error`.
- `FilePath`: percorso del file di log. Con `pcmonitor-.log` Serilog aggiunge la data in base al rolling interval.
- `RollingInterval`: rotazione del file, per esempio `Day`, `Hour`, `Month`.
- `RetainedDays`: per quanti giorni conservare i file.
- `FileSizeLimitBytes`: dimensione massima del singolo file prima del roll.

### LogStaticFiles

`LogStaticFiles` espone via HTTP la cartella dei log:

```json
"LogStaticFiles": {
  "Enabled": true,
  "RequestPath": "/logs",
  "DirectoryPath": "logs",
  "EnableDirectoryBrowsing": true
}
```

- `Enabled`: abilita l'esposizione HTTP dei log.
- `RequestPath`: path HTTP da usare, per esempio `/logs`.
- `DirectoryPath`: cartella da esporre. Se assente, viene usata la cartella del `FilePath` di `AppLogging`.
- `EnableDirectoryBrowsing`: abilita la navigazione della cartella dal browser.

### Kestrel

`Kestrel` configura l'endpoint HTTP:

```json
"Kestrel": {
  "Endpoints": {
    "Http": {
      "Url": "http://0.0.0.0:5000"
    }
  }
}
```

Usare `0.0.0.0` rende il servizio raggiungibile anche da altri host della rete, se firewall e regole di rete lo consentono. Per limitarlo alla macchina locale usare `http://localhost:5000`.
