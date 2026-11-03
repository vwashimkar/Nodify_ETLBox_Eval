# Lessons learned (first POC)

Read this before touching the editor, the CSV engine, DLL generation, or when a Copilot result looks too good. Everything here was seen in this project. Tags: **[P]** proven by a test or run, **[A]** from the API dump, **[U]** not verified.

## 1. Working with Copilot

- A Copilot hang once deleted `EVALUATION.md`. Restore with `git restore EVALUATION.md`. Documents (`EVALUATION.md`, `README.md`, `docs/`) are never in a prompt's scope unless the prompt says so; every prompt has a "do not touch" list.
- A "connection reset" or a "flagged prompt" message does not mean the work failed or succeeded. Check `git status` and `git diff` before assuming either. Two prompts were flagged for no real reason; splitting the prompt and starting a new chat fixed it.
- Copilot cannot click through a WPF window. When it says a drag or a dialog works, that is a guess. Ask it for what it expects, then check by hand.
- A result that cannot be reproduced is not a result. Copilot's DLL-mode run reported 43 pass / 5 fail / 4 observed; running the same command gave 48 / 0 / 4. On 2026-11-02 the app itself gave exactly 43 / 5 / 4 when the trial key had expired, so a missing or invalid licence in Copilot's run is the likely cause. Not proven.
- Agent reports can contradict the raw output: Claude Code's first report said 41 of 41 tests passed while the same test hung 20 of 20 runs when run alone. Check numbers against the raw output and repeat anything timing-dependent.
- Save every file before building. Two "build errors" were unsaved edits.

## 2. Nodify 7.3.0

- XAML namespace: `https://miroiu.github.io/nodify`.
- Do not put the editor inside a `ScrollViewer`. Nodify owns pan and zoom.
- The `SelectedItems` binding re-enters when the bound collection is changed inside a selection handler. Copy the collection first, change the copy, and never clear and refill the bound list inside the handler. [P]
- Connecting nodes by dragging needs three things. The editor's `PendingConnection` must be set when the drag starts (bind `ConnectionStartedCommand` to a command that calls `StartConnection`). The completed command receives two connector view models, which must be resolved and ordered output to input. The new `ConnectionViewModel` must be added to the collection bound to `Connections`. Without the first, no line follows the mouse and the drop is ignored. [P; check `MainWindow.xaml` and `EditorViewModel.cs`]
- Give connection lines and connector rings an explicit brush. On a dark canvas the default looked black and invisible. [P]
- Open defect: the Light and Dark theme choices are inverted. Fix in Stage 0.

## 3. ETLBox 3.11

- A delimiter other than a comma is set with `csvSource.Configuration.Delimiter`. Typing `\t` as text must be mapped to a real tab. [P]
- A `RowTransformation` that returns null drops the row. [P]
- `JsonDestination` and `JsonSource` work through files, so the first POC used temporary files. [P]
- The free edition stops at 5,000 rows **read** from a connector, not rows written. A 12,000-row file failed at the 5,001st row read. Tests with 3,000 to 4,000 output rows still failed without a licence. [P] The exact 5,000 and 5,001 boundary was never tested. [U]
- An expired trial key gives its own error (`The registered trial key expired. The expiration date was: 2026-11-01. Please request a new license key.`). With it, the 43 tests that read small files still passed and the five volume tests (T048 to T052) failed. [P, 2026-11-02]
- The licence file `etlbox.lic` is copied next to `ETLBox.dll` by the project file when it exists. It is in `.gitignore`. Never read it, copy it, embed it, or print it.
- An unterminated quote makes the whole run fail with a clear error, and no earlier valid rows come back. [P, test T041]
- **One parser.** The first POC checked headers by hand (case-insensitive) but looked rows up exactly, so a file with `Location` instead of `location` returned zero rows with no error. [P, test T047] Preview and run must use the same ETLBox reading code.
- `reconciles=true` in the test output only means no row vanished after ETLBox read it. It was true for wrong results too. Do not read it as "correct".
- **Never hand a caller's `CancellationToken` to ETLBox.** A cancel that lands while ETLBox is still starting the network makes `Network.ExecuteAsync(token)` wait forever. Reproduced 2026-11-03: a token cancelled before the call, or 1 ms after the start, hung the call for more than 10 s; the original test hung 20 of 20 runs when run alone, yet the full suite passed once by luck. The cause inside ETLBox was not found. A cancel after rows are flowing (the only case probed on 2026-11-02) ends normally. [P] Fix used in `CsvPreviewReader`: check the caller's token before starting and on every row, and cancel a token that the reader owns from inside the row step; ETLBox only ever sees that token. Use the same pattern for the full run. Known limit: the caller's cancel is noticed only when the next row arrives [U].
- A test that passes once proves nothing about a race. Give every test that awaits ETLBox a timeout and repeat the cancel cases (50 times each) so a hang fails the test instead of freezing the run. [P]
- In the 3.11.1 API dump, `CsvSource` has `Encoding`, `PrepareHeaderForMatch`, `Cancel`, `ExecuteAsync(CancellationToken)`, `LinkErrorTo`, `ProgressCount` and `OnProgress`. [A] None of them has been exercised. [U]

## 4. Generating a DLL with Roslyn

- Compile against the .NET SDK **reference pack** (`Microsoft.NETCore.App.Ref`) plus the non-framework packages. Never reference the runtime implementation assemblies; consumers then fail with CS0012 about `System.Private.CoreLib`. [P]
- Regenerating into a folder where the DLL is loaded fails because the file is locked. Use a new folder, or run `dotnet build-server shutdown`. [P]
- The Generate DLL button once wrote its output into the project's test-data folder. It now starts in Documents and refuses any folder at or under a `.csproj` or `.sln`. [P]
- The generated DLL needs the ETLBox packages and, above 5,000 rows read, a licence file at run time.
- Baked-in text settings are escaped with `SymbolDisplay.FormatLiteral`. The generator embeds the engine source files as resources, so every new engine file must be added to that list.
- The same 52 tests ran in the app and through the DLL with identical results (48 pass, 0 fail, 4 observed). [P]

## 5. Process

- Tests have three kinds of expectation: strict, observe, and error expected. A mismatch in a strict test is a failure; an observe test records what happens. Rows are identified by a SHA-1 of their ids.
- Claims in `EVALUATION.md` carry evidence tags, and several earlier claims were corrected against the raw results. Do the same here: a claim without evidence is marked [U].
- The branch is `master`, not `main`. Keep `etlbox.lic`, `bin`, `obj`, `.vs` and `docs/archive/` out of git.
- Roslyn is allowed only inside `Engine/Generation/`.
