# PdmApproveConvert

SOLIDWORKS PDM add-in. When a file changes state into a "manager approved" state, it starts the
PDM tasks you choose (PDF from drawings, STEP from parts/assemblies). Approving an assembly also
converts the parts and sub-assemblies it references.

It only **queues** tasks. The PDM Task Host does the conversion, exactly like Tasks > Convert in
Explorer, so output folders, task accounts and permissions stay as they are today.

## Download

Get `PdmApproveConvert_v0.1.0.zip` from the
[Releases page](https://github.com/trakyalilter/PDMConverter/releases). It holds `PdmApproveConvert.dll` (the
add-in), `Probe\Probe.exe` (lists your task and state names), `config.sample.xml` and this README.
**v0.1.0 is a pre-release: starting a task from inside PDM has not been proven yet. Follow the pilot test plan
below before using it for real work.**

Built and tested against SOLIDWORKS PDM 2018 (18.5 clients, 18.3 server). Other versions are untested.

## Status

| Piece | State |
|---|---|
| Builds, single DLL (PDM interop embedded) | verified |
| Reads real vault: file state, assembly reference tree, task list | verified on CNC vault (dry run) |
| Rules and task-name matching (`PDF  file from 2D Drawing` has two spaces) | verified (dry run) |
| Inert without config, pilot name lock | verified |
| Starting a task (`RunTask`) | **UNPROVEN**. Fails from outside PDM (below). This pilot is the first real test |
| Real PDM `PostState` event (which fields hold file id / folder id) | **UNPROVEN**, dry run feeds assumed values |
| Installing through the PDM Administration tool | **UNPROVEN** |

## Safety design

* The add-in is installed **vault-wide**, but does **nothing** on a PC that has no
  `C:\ProgramData\PdmApproveConvert\config.xml`. Only the pilot PC gets a config.
* `<OnlyNamesStartingWith>CLAUDE_TEST</OnlyNamesStartingWith>` limits it to files named `CLAUDE_TEST*`,
  even on the pilot PC. Real approvals are ignored.
* `<DryRun>true</DryRun>` logs what it would do and starts nothing.
* The log line `calling RunTask ...` is written **before** the call. If Explorer dies, that line is the evidence.
* The riskiest call is `RunTask`: from a normal exe it shows `String File Error ... "<EXENAME>"` and then
  dies with an access violation in USER32.dll. Inside Explorer it may work, but if it does not, Explorer on
  the pilot PC can crash. Use a PC and a moment where that costs nothing.

## Pilot test plan

Needs: a PDM admin (installs the add-in), a user allowed to do the *Manager Approved* transition, a
running Task Host, and the approving user allowed to launch the STEP task
(Administration > Tasks > `STEP file from 3D Model` > Permissions).

**1. Test file.** In Explorer create a folder `CLAUDE_TEST_PILOT`, copy a small real part into it, rename it
`CLAUDE_TEST_PART.SLDPRT`. Use a part that is not referenced by anything real.
Heads-up: the STEP task writes `CLAUDE_TEST_PART.STEP` to the task's configured output folder
(check it under Administration > Tasks; it may be the ERP integration folder). Delete that file afterwards.

**2. Config on the pilot PC only.** Copy `config.sample.xml` to
`C:\ProgramData\PdmApproveConvert\config.xml`. Leave `DryRun=true` and the `CLAUDE_TEST` prefix.

**3. Install.** PDM Administration > Add-ins > New add-in > `PdmApproveConvert.dll`. Restart Explorer on the
pilot PC (log off/on if it does not pick it up). If the PC does not load it, run
`regasm /codebase PdmApproveConvert.dll` there as administrator.

**4. Dry run.** Put `CLAUDE_TEST_PART.SLDPRT` through the approve transition.
Read `%LOCALAPPDATA%\PdmApproveConvert\addin.log` of the approving user. Expect:
```
PostState received: 1 item(s); user=<windows user> pc=<pc> process=<exe>; config found, DryRun=True
PostState raw: id1=<file id> id2=<folder id> ...
queue 'STEP file from 3D Model' <- CLAUDE_TEST_PART.SLDPRT (v1)
DRY RUN: would run task 'STEP file from 3D Model' on 1 file(s)
```
If the ids look wrong, the PostState layout differs from the assumption. Stop here and send me the log.

**A state change left no line in the log?** Every state change this PC/user performs is logged as
`PostState received`, even when the add-in is inert or ignores the file. No line means the change happened
somewhere else, so read the log of the PC and Windows user that actually clicked the transition:
* `LOADED v0.2.0 ... config found` at the top: add-in loaded, config present.
* `LOADED ... config MISSING (add-in inert)` or `inert: no config file`: copy `config.xml` to that PC.
* No `LOADED` line at all today: add-in is not loaded there. Restart Explorer / log off and on.
* `skip '<file>': state '<x>' is not an approved state`: it saw the change, the new state is just not a trigger.
  Add that state to `<ApprovedStates>` if it should trigger.

**5. Live.** Set `<DryRun>false</DryRun>`, put the file back to an earlier state, approve again.
Watch the log. Expected: `calling RunTask ...` then `STARTED task ...`, then a new entry in the Task list.
* Log ends at `calling RunTask` and Explorer is gone: RunTask is not usable inside PDM either. Stop, send me the log.
* A dialog appears: copy its text.
* `STARTED` but no task appears: send me the log and the task list.

**6. Rollback.** Administration > Add-ins > remove it. Delete `C:\ProgramData\PdmApproveConvert` and the
`CLAUDE_TEST_PILOT` folder and its output file.

Send me: `addin.log`, and what the Task list showed.

## Config

`C:\ProgramData\PdmApproveConvert\config.xml`, see `config.sample.xml`. Re-read at every state change.
Defaults from the CNC vault: trigger states `Manager Approved`, `Library-Manager Approved`,
`SWE Managers Approved`; `.sldprt/.sldasm` run `STEP file from 3D Model`, `.slddrw` run
`PDF file from 2D Drawing`; assembly children are accepted in approved states or `Send To Erp-Only PART!`,
`Send Library Part to Erp-Only PART!`, `FILES SENT TO ERP`.
For the real rollout delete `OnlyNamesStartingWith`, set `DryRun` false, and copy the config to the
PCs where managers approve.

## Probe.exe

```
Probe.exe CNC                                  list tasks, workflows, states
Probe.exe CNC --find "%.sldasm" "Manager Approved"   files in a state (SQL wildcard %)
Probe.exe CNC --file "C:\PDMView\CNC\x.SLDASM"       state + reference tree
Probe.exe CNC --simulate "C:\PDMView\CNC\x.SLDASM"   add-in logic as if approved; needs a config (see below)
```
`--simulate` needs a config: set `PDMAPPROVECONVERT_CONFIG` to a config.xml path first. Keep `DryRun=true`.
`--run` exists but does NOT work (RunTask outside PDM, see above); do not use it.

## API notes (PDM 18.5) - do not "simplify" away

* `IEdmWorkflow6.GetNextState()` **corrupts the heap**: the next unrelated COM call or GC dies with
  `0xC0000374`. The add-in never enumerates states; Probe does it in a child process.
* `IEdmTaskMgr.RunTask()` from an ordinary exe: see Safety design.
* Task names can contain double spaces; matching ignores that.
* `CreateUtility` through embedded interop returns `dynamic`; code assigns to `object` first.

## Build

Needs the .NET SDK, the SOLIDWORKS PDM client, and TlbImp.exe (comes with Visual Studio).
```
powershell -File tools\make-interop.ps1                 # once: generates lib\Interop.EdmLib.dll from the PDM type library
dotnet build PdmApproveConvert.csproj -c Release        # bin\Release\net48\PdmApproveConvert.dll
dotnet build tools\Probe\Probe.csproj -c Release
```
The interop assembly comes from SOLIDWORKS' type library, so it is generated on your PC and not committed.
It is embedded into the built DLL, so the add-in is a single file.

## License

None chosen yet. Until one is added, all rights are reserved by the author.
