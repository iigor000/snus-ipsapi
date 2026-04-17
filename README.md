# Industrial Processing System API

Ovaj projekat implementira thread-safe, asinhroni producer-consumer sistem sa prioritetnim redom, retry mehanizmom, event logovanjem i periodičnim XML izveštajima.

## Struktura projekta

- Models.cs
- ProcessingSystem.cs
- Program.cs
- SystemConfig.xml
- snusProj.csproj

## Brzi start

1. Proveri da SystemConfig.xml postoji u izlaznom folderu aplikacije (bin/Debug/net9.0) ili da je kopiran pri build-u.
2. Pokreni:

```bash
dotnet build
dotnet run
```

## Konfiguracija: SystemConfig.xml

Primer formata:

```xml
<?xml version="1.0" encoding="utf-8"?>
<SystemConfig>
  <WorkerCount>5</WorkerCount>
  <MaxQueueSize>100</MaxQueueSize>
  <Jobs>
    <Job Type="Prime" Payload="numbers:10_000,threads:3" Priority="1"/>
    <Job Type="IO" Payload="delay:1_000" Priority="3"/>
  </Jobs>
</SystemConfig>
```

### Objašnjenje polja

- WorkerCount: broj worker task-ova koji obrađuju red.
- MaxQueueSize: maksimalan broj job-ova u prioritetnom redu.
- Jobs/Job: inicijalni poslovi.
- Type: Prime ili IO.
- Payload:
  - Prime: numbers:<limit>,threads:<broj_niti>
  - IO: delay:<milisekunde>
- Priority: manji broj znači veći prioritet.

Napomena: više se ne koristi fallback konfiguracija. Ako je konfiguracija neispravna ili ne postoji, aplikacija baca grešku i prekida rad.

## Models.cs

### JobType

Enum sa tipovima:
- Prime
- IO

### Job

Model posla:
- Id: jedinstveni identifikator.
- Type: tip obrade.
- Payload: ulazni parametri obrade.
- Priority: prioritet (1 je viši od 2, itd).

### JobHandle

Povratna vrednost metode Submit:
- Id: identifikator job-a.
- Result: Task<int> koji se završava rezultatom ili greškom.

### JobEventArgs

Podaci za event-e JobCompleted i JobFailed:
- JobId, Type, Result, Status, Attempt, ErrorMessage.

## ProcessingSystem.cs

ProcessingSystem je centralni thread-safe servis.

### Glavne odgovornosti

- Učitavanje konfiguracije iz XML-a.
- Inicijalizacija worker task-ova.
- Thread-safe prijem job-ova kroz Submit.
- Obrada po prioritetu.
- Idempotentnost (isti Job Id se ne izvršava više puta).
- Retry i timeout logika.
- Event-driven logovanje.
- Periodično generisanje XML izveštaja.

### Thread-safe elementi

- PriorityQueue<Job, (Priority, Sequence)>: prioritetni red sa FIFO ponašanjem za isti prioritet preko sequence broja.
- SemaphoreSlim _queueLock: štiti pristup redu.
- SemaphoreSlim _queueSignal: signalizira dostupnost posla worker-ima.
- ConcurrentDictionary kolekcije:
  - _knownJobs
  - _handles
  - _results
  - statistike po tipu

### Submit(Job job)

Tok:
1. Provera null.
2. Idempotentnost: ako handle za isti Id već postoji, vraća se postojeći.
3. Kreira se TaskCompletionSource i JobHandle.
4. Pod lock-om se proverava MaxQueueSize.
5. Ako je red pun: posao se odbija i Result dobija exception.
6. Ako ima mesta: enqueue po prioritetu i signal worker-u.

### Worker petlja

WorkerLoopAsync:
- Čeka signal da postoji posao.
- Bezbedno dequeue-uje sledeći prioritetni posao.
- Poziva ProcessWithRetryAsync.

### Obrada i retry

ProcessWithRetryAsync:
- Maksimalno 3 pokušaja.
- Svaki pokušaj ima timeout 2 sekunde.
- Ako uspe:
  - upis statistike uspeha,
  - setovanje Task rezultata,
  - JobCompleted event.
- Ako timeout/greška:
  - prvi i drugi put status FAILED,
  - treći put status ABORT,
  - rezultat se završava exception-om,
  - JobFailed event.

### Tipovi obrade

Prime:
- Payload parsira numbers i threads.
- threads se clamp-uje na [1,8].
- Računa broj prostih brojeva do limit vrednosti koristeći Parallel.For.

IO:
- Payload parsira delay.
- Simulira IO kašnjenje preko Thread.Sleep unutar Task.Run.
- Vraća random broj 0-100.

### Event sistem i log fajl

- JobCompleted i JobFailed su async event-i.
- Podrazumevano su pretplaćeni na WriteLogAsync.
- Svaki event upisuje red:
  - [DateTime] [Status] JobId, Result

### API metode

- GetTopJobs(int n): vraća prvih N iz aktivnog reda sortirano po prioritetu.
- GetJob(Guid id): vraća job iz poznatih job-ova.
- GenerateReportAsync(): ručno generisanje XML izveštaja.

### Periodični izveštaji

ReportLoopAsync:
- Svakog minuta pravi izveštaj.
- Izveštaj sadrži:
  - broj izvršenih po tipu,
  - prosečno vreme po tipu,
  - broj neuspešnih po tipu.
- Čuva poslednjih 10 fajlova kao report_0.xml do report_9.xml (ring overwrite).

## Program.cs

Program demonstrira rad sistema:

1. Učitava putanju do SystemConfig.xml.
2. Ako ne postoji, baca grešku (bez fallback-a).
3. Učitava WorkerCount i koristi ga kao broj producer task-ova.
4. Kreira ProcessingSystem.
5. Dodaje lambda pretplate na JobCompleted i JobFailed za konzolni ispis.
6. Producer task-ovi nasumično dodaju job-ove.
7. Čeka završetak producer-a i svih Result task-ova.
8. Ručno generiše završni izveštaj.

## Važne napomene

- Ako želiš striktno time-independent testiranje, preporuka je poseban test projekat sa TaskCompletionSource/SemaphoreSlim bez oslanjanja na Thread.Sleep u test kodu.
- U produkcionom scenariju, korisno je dodati validaciju sheme XML-a (XSD) i strukturisano logovanje.
- Da bi SystemConfig.xml uvek bio uz izvršni fajl, možeš dodati CopyToOutputDirectory u csproj.

## Primer izlaza

- COMPLETED: <guid> -> <result>
- FAILED: <guid> (attempt 1)
- ABORT: <guid> (attempt 3)

Log i izveštaji:
- processing.log
- reports/report_*.xml
