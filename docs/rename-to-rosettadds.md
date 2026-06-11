# Rename plan: rosettadds -> ROSettaDDS

## Naming rules

- Product / C# identifier name: `ROSettaDDS`
- Namespace root: `ROSettaDDS`
- CLI / file system / package id / assembly file name: `rosettadds`
- Generated-code banner and user-facing prose also use the new name

## Scope

- Solution, project, assembly, package, and directory names
- C# namespaces and symbol references under `ROSettaDDS.*`
- Unity asmdef / UPM metadata / related test assembly names
- CLI name `rosettadds-genmsg`
- Docs, samples, tests, and metrics strings that expose the old name

## Non-goals

- No compatibility shim for the old `ROSettaDDS.*` namespace
- No fallback executable names
