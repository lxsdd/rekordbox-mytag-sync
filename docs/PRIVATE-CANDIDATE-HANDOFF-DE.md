# rekordbox MyTagSync — Entscheidung zur einmaligen privaten Testübergabe

**Stand: 21.10.2026. Kein Release, keine Einwilligung, keine Verteilung.**
Diese Unterlage ersetzt weder die Microsoft-Verträge noch eine juristische Prüfung. Sie beendet aber die Suche nach der grundsätzlichen Weitergabeberechtigung der von Microsoft mit WPF ausgelieferten Komponenten: Die offiziellen Rechtefamilien und der technische REDIST-Zweck sind belegt.

## A. Verifizierte technische Lizenzlage

1. **Genaues .NET 10.0.13:** Im Original von `dotnet/runtime@v10.0.13:docs/project/licensing-assets.md` (Git-Blob `d0484f8733fc87d785b096ce770fa94453b64dfa`) bestimmt Microsoft die **.NET Library License** für Windows-*Produktdistributionen und Runtime-Packs* und verlangt Lizenztext und Third-Party-Notices. Das unterscheidet solche Distributionen von MIT-lizenzierten einzelnen Bibliothekspaketen. Dies belegt für die eingesetzte Runtime-Version die zutreffende **Lizenzfamilie**, nicht die Erfüllung der Vertragsbedingungen.
2. **Native Ausnahmen:** `dotnet/core:license-information-windows.md` (Git-Blob `bbba73b9a0b553b1dffd2b4299fc5eb203e195fb`) ordnet u. a. den Single-file-Laufzeithost sowie `wpfgfx_cor3.dll` und `vcruntime140_cor3.dll` der .NET Library License, `D3DCompiler_47_cor3.dll` dagegen der Windows SDK License zu. Normale .NET-Bibliotheken sind überwiegend MIT. Der Bericht selbst ist nur ein Klassifikationshinweis, **keine Lizenz**.
3. **Genaues WPF 10.0.13:** `dotnet/wpf@v10.0.13:Documentation/redistributables.md` (Git-Blob `b90e9ce29b7500d3ebf83649cb552c0a6049b131`) erläutert die ausdrücklich beabsichtigte Weitergabe des SDK-Compilers in umbenannter Form mit WPF.
4. **SDK-REDIST:** Microsofts offizielle [REDIST.TXT-Liste](https://learn.microsoft.com/en-us/legal/windows-sdk/redist) enthält `Windows Kits/10/Redist/D3D/x64/d3dcompiler_47.dll` ausdrücklich für klassische Windows-Anwendungen. [SDK-Vertragsbedingungen](https://learn.microsoft.com/en-us/legal/windows-sdk/license) gelten weiterhin. Das bei uns archivierte Original `microsoft/win32metadata:licenses/sdk_license.txt` nennt die ältere Kennung `WIN10SDK.RTM.AUG_2018_en-US`; seine konkrete Fassungsidentität zur gebündelten Compiler-Binärdatei ist **nicht** bewiesen.
5. **Tatsächliche Anwendung:** Windows 11, WPF `net10.0-windows`, x64, selbstständig ausführbare Anwendung mit eigenständiger Funktionalität. Das jüngste erfolgreiche FULL prüfte 388 paketierte Dateien, ihre Originalquellen, elf Lizenz-/NOTICE-Texte und null *fehlende Quellen-IDs*. **Null fehlende IDs ist keine juristische Freigabe**.

## B. Welche Bedingungen vor tatsächlicher Übergabe praktisch erfüllt werden müssen

Aus [Microsoft .NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) und [Windows-SDK-Bedingungen](https://learn.microsoft.com/en-us/legal/windows-sdk/license):

- Microsoft-Objektcode nur als **Teil unserer eigenen Windows-Anwendung**, nicht isoliert anbieten; SDK-Code muss eine wesentlich eigenständige Anwendung begleiten; keine Microsoft-Unterstützung oder Markeninhaberschaft behaupten.
- Unveränderte relevante Lizenztexte, Original-Copyrights und Third-Party-Notices zusammen mit dem Testpaket bereitstellen. Dazu gehören auch SQLite3MC-/SQLitePCLRaw-Rechte und die WPF-/Windows-Ausnahmen. Keine Benutzerbibliothek, Musik, rekordbox-Datenbank, SQLCipher-Schlüssel oder geschützte Drittdatei verteilen.
- Die vom Microsoft-Vertrag geforderten hinreichend schützenden **Endnutzer-/Weitergabebedingungen** tatsächlich vereinbaren. Der Weitergebende muss die ausdrücklich genannten **Freistellungs-/Haftungs-, Datenschutz- und Exportbedingungen** beurteilen; eine Checkbox oder ein automatisch erzeugtes CI-PASS reicht dafür nicht.
- Vorab die passende tatsächliche SDK-Lizenz/REDIST-Fassung und die Nutzungsbedingungen der Microsoft-Komponenten nachvollziehbar bestätigen; die Umbenennung ist Microsoft-seitig in WPF dokumentiert, darf aber nicht als generelle Befugnis zu beliebigen Binäränderungen missverstanden werden.
- Deshalb keine Veröffentlichung oder Weitergabe durch uns, bevor der Rechte-/Nutzungsentscheid durch den Rechteinhaber/Weitergebenden ausdrücklich getroffen wurde.

## C. Konkreter kostenfreier Ein-Empfänger-Übertragungsweg (noch **nicht** freigegeben)

**Bevorzugt:** Ein **Draft-Release im bestehenden GitHub-Repository**, nicht ein öffentliches Actions-Artefakt, regulärer Release, Gist oder ungeschützter Dateilink.

Die [GitHub-Releases-API](https://docs.github.com/en/rest/releases/releases) dokumentiert: Öffentliche Release-Listen sind für alle sichtbar, **Draft-Releases dagegen nur für Konten mit Push-Zugriff**. [GitHub Release Assets](https://docs.github.com/en/rest/releases/assets) können danach authentifiziert heruntergeladen werden. Die [Größenbegrenzung](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases) beträgt 2 GiB pro Datei; das aktuell getestete MyTagSync-ZIP ist kleiner. Keinerlei externes Hosting oder kostenpflichtiger Runner erforderlich. **Das ist ein dokumentiertes Zugriffsmodell, noch kein Live-Negativtest.**

**Nach gesonderter ausdrücklicher Eigentümerfreigabe**, in dieser Reihenfolge:

1. Exakt aktuellen, FAST- und FULL-qualifizierten Commit feststellen und Schreib-/Upload-Sperren bis zum positiven Vorabtest unverändert lassen. Kein Tag oder öffentlicher Release.
2. Ein zeitweiliges **Draft** ausschließlich mit harmloser synthetischer TXT-Datei erzeugen. Als eingeloggter Repo-Eigentümer Zugriff nachweisen; ohne Anmeldung und mit Lesezugang ohne Push den Draft samt **Asset-Download** tatsächlich zurückweisen lassen (kein Rate-/Login-Fehler fälschlich als Sicherheitsnachweis werten). Ebenso veröffentlichte Releases und Tags kontrollieren.
3. Nur wenn die Negativprüfung belastbar bestanden wurde, nach gesonderter erforderlicher Lizenz-/Owner-Entscheidung genau **ein** SHA-gebundenes Test-ZIP samt vollständigen unveränderten Originalhinweisen als Draft-Asset vom qualifizierten Windows-Runner bereitstellen. Keine Veröffentlichung des Drafts und kein manuelles Teilen eines öffentlichen Asset-Links.
4. Mit authentifiziertem Eigentümerzugang herunterladen, den **inneren ZIP-SHA-256**, `SOURCE_SHA.txt`, Manifest und alle Datei-Hashes unabhängig prüfen. Anschließend den Draft/die Assets löschen. **Drafts verfallen nicht automatisch**; bis zur nachgewiesenen Löschung darf nicht behauptet werden, der Transfer sei automatisch zeitlich begrenzt.
5. Auf dem privaten Windows-11-Laptop folgt **nur die read-only-Abnahme** in [WINDOWS11-READONLY-ACCEPTANCE.md](WINDOWS11-READONLY-ACCEPTANCE.md). `Apply`, `Restore` und alle realen DB-, Audio-, CUE- oder Backup-Schreibzugriffe bleiben ausgeschlossen.

**Fehlerfall:** Anonyme Asset-Abfrage liefert Inhalt, draft wird sichtbar/veröffentlicht, Rechte sind unklar, Blob/ZIP-Hash stimmt nicht, Tag entsteht unerwartet oder Besitzer hat noch nicht zugestimmt ⇒ Draft löschen und keinerlei Binärdaten hochladen.

## D. Tatsächlich noch menschlich erforderlich

**Entscheidung 1:** Hat der Eigentümer nach Sichtung der Originalverträge die Weitergabebedingungen einschließlich Microsoft-Freistellung, Pflicht zu schützenden Endnutzerbedingungen, Copyright/Datenschutz/Export sowie konkreter SDK-Fassung akzeptiert bzw. rechtlich prüfen lassen? Das ist eine Vertragsentscheidung, die CI und Assistent nicht stellvertretend treffen können.

**Entscheidung 2:** Erlaubt der Eigentümer **explizit** den genannten nichtöffentlichen Draft-/TXT-Test und – ausschließlich bei dessen PASS und geklärten Rechten – die einmalige private Ablage eines exakt qualifizierten ZIP? Auch ein GitHub-Draft ist ein Release-Objekt und bedarf nach der Projektvorgabe ausdrücklicher Freigabe.

**Entscheidung 3:** Später die echte, gebündelte **Windows 11 x64 read-only-Endabnahme** auf dem eigenen DJ-Laptop; keine vorgezogenen Einzeltests.

Die automatisierte Quellenprüfung bleibt `distributionLicenseStatus=REVIEW_REQUIRED_NOT_AUTHORIZED` und `completeness=NOT_ESTABLISHED`. Weder dieser Entscheidungsstand noch die laufende Dokumentationsänderung aktiviert Releases, Testuploads, App-Writes oder zusätzliche Workflows.


## E. Nach dem bestandenen anonymen Draft-Test: tatsächlicher Einmal-Transfer per GitHub Actions

**Vorbereitung durch den Entwickler:** Der getrennte Workflow
[`OWNER-PRIVATE-READONLY-CANDIDATE`](https://github.com/lxsdd/rekordbox-mytag-sync/actions/workflows/owner-private-readonly.yml)
ist ausschließlich **manuell** vom Repository-Eigentümer `lxsdd` auf `main`
auslösbar; GitHub gibt ihm für *diesen* manuellen Windows-Job den erforderlichen
`contents: write`-Token, während FAST und FULL weiterhin ausschließlich
lesende Rechte besitzen. Er übernimmt die schon bestehenden FULL-Prüfschritte
(keinen unqualifizierten Schnell-Build), verwirft alle Fremd-Commits und
verlangt drei erfolgreich durchlaufene FULL-Windows-Jobs auf exakt demselben
aktuellen Main-SHA. GitHub-Actions-Artefakte werden **niemals** hochgeladen.

Der Einmal-Lauf baut zunächst zweimal deterministisch, verifiziert die echten
388-plus Bundle-/NOTICE-Dateien und die Read-only-WPF-Schreibsperre,
vergleicht den **gesamten neu erstellten ZIP-SHA-256** mit dem zuvor
protokollierten, erfolgreichen FULL-Lauf und legt **erst danach** einen neuen
`draft=true`-Release an. In diesen werden ausschließlich ZIP,
SHA-256-Sidecar und Qualifikations-JSON übertragen; ein öffentlicher Release
wird weder angelegt noch veröffentlicht. Der Workflow prüft unmittelbar
erneut `draft=true`, drei konkrete Asset-Identitäten und anonym `404` für
Release-Seite und direkten ZIP-Download. Jede Abweichung stoppt den Vorgang.

**Dein nächster notwendiger manueller Schritt, erst nach grünem aktuellem
FAST/FULL:** Öffne den oben verlinkten Workflow und starte `Run workflow`
mit `Branch=main`. Die vier Eingaben werden vorab hier im Chat anhand der
konkreten GitHub-Ergebnisse genannt: `source_sha`, `full_run_id`,
`expected_zip_sha256` und `legal_ack`.

Für `legal_ack` ist ausschließlich der selbst eingetippte genaue Text
`I_ACCEPT_PRIVATE_MICROSOFT_DOTNET_AND_SDK_DISTRIBUTION_DUTIES` zulässig.
**Bitte nur angeben, wenn du die geltenden [Microsoft .NET Library-Bedingungen](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm),
[Windows-SDK-Vertragsbedingungen](https://learn.microsoft.com/en-us/legal/windows-sdk/license)
und die oben erläuterten Schutz-, Copyright-, Export- und Haftungs-/Freistellungspflichten
gelesen hast und als Weitergebender akzeptierst.** Der Assistent kann diese
rechtsgeschäftliche Erklärung nicht für dich abgeben. Ohne sie verweigert
der Workflow das Erstellen eines Releases.

Die spätere private Draft-Löschung ist **manuell nach Download und Abnahme**
erforderlich, weil GitHub-Drafts keine automatische Ablaufzeit besitzen.
Der bereits getestete harmlose Draft
`mytagsync-draft-access-test-20261021` ist separat und darf nicht
versehentlich veröffentlicht werden; er kann nach der erfolgreichen
Dokumentation gelöscht werden. `Apply`, `Restore`, produktive
`master.db`-, CUE-, Musik- und Backup-Schreibzugriffe bleiben gesperrt.
