# Windows 11: sichere MyTagSync-Endabnahme (noch nicht freigegeben)

**Zielplattform:** Windows 11 x64 auf dem tatsächlichen DJ-Laptop; rekordbox 6/7 mit XDJ-RX3, foobar2000 und foobar-Bridge. **Windows 10 ist nicht die Zielplattform dieses Benutzers.**

Diese Anleitung ist ein **Abnahmevertrag**, keine Freigabe zum produktiven Schreiben. Insbesondere führt die aktuelle öffentliche Build-Pipeline zwar vollständige Win-x64-Tests aus, GitHub `windows-latest` (Windows Server) und `windows-2022` (Windows Server 2022) sind **keine** echte Windows-11-Client-Qualifikation.

## Voraussetzungen für die einzige abschließende Laptop-Prüfung

1. Exakten `main`-Commit und dazu **FAST=SUCCESS**, **FULL=SUCCESS** einschließlich `qualify-portable-candidate` und `older-windows-api-smoke` dokumentieren. Ein späterer Commit erfordert eine neue SHA-gebundene Qualifikation.
2. **Rechte-/Verteilungsfreigabe** sowie einen sicheren, überprüfbaren Übergabeweg für *ein* Kandidatenpaket abschließen (siehe [Distribution gate](DISTRIBUTION-GATE.md)). `FULL`-Upload und `PROMOTE-RELEASE` bleiben bis dahin gesperrt. Ein nur im temporären GitHub-Runner gebautes ZIP steht nicht zum Download bereit.
3. Das tatsächlich erhaltene Kandidaten-ZIP anhand seines eigenen **inneren ZIP-SHA-256** und `SOURCE_SHA.txt`/`candidate-manifest.json` kontrollieren. Der äußere GitHub-Transport-ZIP-Hash ist ein **anderes** Objekt. Keinen Hash eines älteren Commits wiederverwenden.
4. Auf dem Windows-11-Gerät im nichtadministrativen Benutzerkontext starten, ohne in produktive Dateien zu kopieren oder Windows-Rechte zu umgehen. Die UI ist nicht als Administrator auszuführen.
5. rekordbox, rekordboxAgent und rbxport vor jeglichem möglichen Schreib-/Restore-Vorgang schließen. Die hier beschriebene Phase ist ausschließlich **read-only**; `Apply`, `Restore` und jede Eingabe einer Schreibbestätigung sind ausdrücklich **nicht** Bestandteil dieses Tests.

## SHA-gebundener lokaler Vorabcheck (erst nach zulässiger privater Übergabe)

Nach Erhalt des freigegebenen Kandidaten prüft
`scripts/Verify-Win11Candidate.ps1` mit den **für denselben Source-Commit**
bestätigten Argumenten `-CandidateZip`, `-ExpectedSourceSha` und
`-ExpectedZipSha` zunächst den äußeren Kandidaten-ZIP-Hash, die SHA- und
Plattform-Angaben des Manifests, sämtliche enthaltenen Datei-Hashes sowie
Pfad-Traversal- und Duplikatprobleme. Erst **danach** extrahiert er in
`%LOCALAPPDATA%\MyTagSync\qualification\<source-sha>` und führt den
synthetischen `--ui-smoke` ohne Administratorrechte aus. Bereits bestehende
Zielverzeichnisse werden nicht überschrieben.

Dieser Vorabcheck benötigt kein installiertes .NET-SDK und greift nicht auf die
echte rekordbox-Bibliothek zu. Der anonymisierte
`READONLY-PREFLIGHT.json`-Bericht besagt ausdrücklich
`realRekordboxAcceptance=PENDING`; die unten stehende reale read-only
Geräteabnahme ist also anschließend weiterhin erforderlich. Der Prüfhelfer
ist **kein** Ersatz für die noch ausstehende Verteilungsfreigabe und enthält
keinen Downloadlink zu einem unveröffentlichten Programm.

## Einmalige read-only Prüfung auf dem DJ-Laptop

| Bereich | Konkretes PASS-/FAIL-Kriterium |
| --- | --- |
| Betriebssystem und Start | Windows 11 x64 (Edition/Build lokal feststellen), portables Programm startet ohne Installation/Adminrechte, fenstergerechtes Verhalten bei DPI/Skalierung |
| UI-Persistenz | Größe, Position, Spaltenbreiten und Filter bleiben über einen Neustart erhalten; Icons, Tooltips und Tastaturbedienung sind nutzbar; erste Tabellenspalten bleiben beim horizontalen Scrollen zugänglich; Touchpad scrollt sinnvoll |
| foobar-Quelle | Der echte Bridge-Snapshot wird automatisch gefunden, `Inspect selected` zeigt nachvollziehbare Quelle/Generation; bestehende Musik/Tags/Cues bleiben unverändert |
| rekordbox-Ziel | `Find libraries` entdeckt die tatsächliche `master.db`; SQLCipher und DBVersion/DBID werden **nur lesend** validiert; ein Alias oder eine Kopie darf nicht fälschlich die reale Datenbank identifizieren |
| Pfadzuordnung | Quell-/Zielwurzeln (ggf. Laufwerks-/SUBST-/UNC-Aliase) werden nachvollziehbar vorgeschlagen; nur unabhängige native VolumeSerial/FileIndex-Identität gilt als physische Bestätigung; ein Dateiname allein genügt niemals |
| Umfang/Ausschlüsse | ContentIDs und Grund jedes Ausschlusses sichtbar; virtuelle CUE-Subsongs nie physische Schreibziele; Duplikate, Kopien und fehlende Dateien bleiben ausgeschlossen; Abweichungen in der echten Bibliothek melden, nicht automatisch korrigieren |
| Tag-Vorschau | `DATE`→Year, `GENRE`→Genre, Mehrfachwerte, optionale Regex-/Filterregeln und bestehende manuelle MyTags werden korrekt differenziert; Apply-Vorschlag ohne Berechtigung verändert nichts |
| No-write-Sicherheit | Suche, Discovery, Inspection, Validate, Path Scan, Preview, Abbruch und Fenster-Schließen dürfen weder `master.db` noch Audiodateien/physische Tags/embedded CUEs verändern. Write-Aktionen bleiben gesperrt, bis später *separat* freigegeben |
| XDJ-RX3 | Nach Beendigung der PC-Prüfung rekordbox regulär starten und Sammlung, My Tags, Cue-Daten und RX3-Browsing unverändert wiederfinden; keine RX3-Firmware-/Geräteänderungen |

### Zusätzliches CUE-/Subsong-Abnahmekriterium (physisch/virtuell)

Die foobar-Bridge exportiert jeden Eintrag mit **`(path, subsong)`**; nur
`subsong=0` ist ein *möglicher* physischer Dateikandidat (nicht automatisch
ein bewiesener Treffer). Ein Eintrag mit `subsong>0` ist ein virtueller
Track — beispielsweise aus einem eingebetteten oder externen CUE, aber nicht
zwingend. Er darf **niemals** einem unabhängigen rekordbox-ContentID oder
einer zusätzlichen physischen Datei gleichgesetzt werden.

- **PASS:** Existieren für einen Audiodateipfad sowohl ein `subsong=0` als
  auch ein oder mehrere virtuelle Tracks, bleibt der physische Pfad in der
  reinen Pfad- und Datei-ID-Diagnose *eindeutig*, sofern keine zweite physische
  Quelle kollidiert. Virtuelle Einträge werden getrennt als ausgeschlossen
  gezählt. Ein tatsächlicher Match erfordert weiterhin native
  `VolumeSerial/FileIndex`-Identität.
- **PASS:** Exportiert die Bridge für ein CUE-Album nur `subsong>0`, erzeugt
  MyTagSync **keinen** erfundenen `subsong=0`-Eintrag, keine physische
  Aliaszuordnung und keinen MyTag-Änderungsvorschlag für diese Segmente.
  Diagnostischer Ausschlussgrund bei einem entsprechenden rekordbox-Ziel:
  `VIRTUAL_SUBSONG_ONLY`.
- **PASS:** Zwei *echte* physische `subsong=0`-Einträge für denselben
  normalisierten Pfad bleiben unaufgelöste Mehrdeutigkeiten. Unterschiedliche
  Dateien mit gleichem Namen oder gleichen Bytes gelten weiterhin nicht als
  identisches Dateiobjekt.
- **PASS:** Selbst ein nicht interpretierbarer Dateipfad eines rein virtuellen
  Eintrags darf die lesende *physische Pfadanalyse* nicht unterbrechen. Diese
  Bedingung wird bereits mit synthetischen Windows-CI-Daten geprüft; sie muss
  auf dem DJ-Laptop **nicht künstlich erzeugt** werden.

Falls die reale Sammlung keine geeignete physisch/virtuell-Kombination
enthält, bitte **keine Musikdateien oder CUEs dafür erstellen oder ändern**.
Der synthetische CI-Nachweis bleibt dann bestehen; die tatsächliche
Quellkonstellation wird nur anonymisiert als `NICHT VORHANDEN` festgehalten.

**Versionsgrenze:** Diese zusätzlichen Diagnosekorrekturen sind erst mit
dem Merge der hierfür qualifizierten Quellcodeänderungen Bestandteil eines
neuen Builds. Der bereits separat privat übergebene Kandidat
`31ff9950f48e7e40828caf6a04846a6a6fda8d28` enthält diese neueren
Änderungen **nicht** und darf dafür nicht als PASS abgenommen werden.

Bei einem FAIL werden **nur lokale, anonymisierte Ergebnisse** weitergegeben (Schritt, erwarteter/tatsächlicher Zustand, Windows-Version, App-Source-SHA); **keine** privaten `master.db`, Musikdateien, ContentIDs, Tokens, Pfade oder vollständigen Debug-Dumps in GitHub-Issues veröffentlichen. Die vollständige Testreihe soll möglichst einmalig und gebündelt stattfinden, nicht in vielen Mini-Runden.

**Grenze dieser Abnahme:** Eine erfolgreiche read-only-Abnahme erteilt **keine** Genehmigung für `Apply` oder `Restore`. Für jede spätere reale Transaktion sind nachvollziehbare Vorschau, neu geprüfte Identität, separates Rolling Backup und eine eigene ausdrückliche Zustimmung notwendig.

## Softwareseitige Nachweise, nicht Client-Abnahme

- Bislang privat übergebener eingefrorener Kandidat (ohne die oben beschriebenen neuesten CUE-Diagnosekorrekturen): `31ff9950f48e7e40828caf6a04846a6a6fda8d28`, [FAST #39644213830](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39644213830) und [FULL #39644673181](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39644673181) SUCCESS. Innerer ZIP-SHA-256 `0fcdb11d190e68b4a3ee714aee1147b4c27bd3114a6521dc4affab1d18e93cb9`; Draft release ID `420369311`, **nicht öffentlich**. Der Eigentümerworkflow stellte alle drei Assets privat mit anonymen 404-Negativtests bereit; der nachträgliche `$LASTEXITCODE`-Statusfehler meldete den erfolgreichen Transfer irrtümlich als FAILURE. Kein Hardwaretest erfolgt.
- Historischer früherer Produkt-`main`: `71939b9d91258f6bb40ef7509e7744a8c7c8d5e7`
- [FAST #39446071413](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39446071413) – SUCCESS, einschließlich nativer foobar-Bridge-Publisher→verschlüsselter SQLCipher-Preview-Test
- [FULL #39446568359](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39446568359) – SUCCESS, inklusive Win-x64 ZIP-Determinismus und ältere Windows-Server-API-Probe
- Innerer, nur temporär im Runner erzeugter ZIP-SHA-256: `bb82d68ff36300efee274017ac09af347c2e43843d53bc6199c6b43a860d2cf0`
- GitHub-Download-Artefakte dieses automatischen FULL-Laufs: **0**, wegen beabsichtigter Verteilungssperre. Kein Nutzer-Download oder Windows-11-Hosttest aus diesen Ergebnissen ableiten.

Diese konkreten Werte sind ein historischer Checkpoint; bei einer neueren Version müssen sämtliche SHA- und Runangaben aktualisiert werden.
