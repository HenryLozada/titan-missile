# TITAN — Game Booster

Boost **medible, reversible y que no pesa**.

## Estructura
- `src/Titan.Engine` — motor: memoria estilo RAMMap/ISLC, congelado/moderación de procesos, CPU sets (P-cores / V-Cache), plan de energía, servicios, journal anti-crash.
- `src/Titan.Cli` — `titan.exe` (NativeAOT, ~3.6 MB) para probar el motor.
- `tests/Titan.Engine.Tests` — tests unitarios.

## Compilar
```bash
dotnet test
# NativeAOT necesita vswhere en el PATH:
PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer" dotnet publish src/Titan.Cli -c Release -r win-x64 -o artifacts/cli
```

## Uso (terminal como Administrador para purge/boost/restore)
```
titan status | topology | plan [--game X]
titan purge [--all --ws --sys --modified --standby --lowpri]
titan boost --game cs2.exe [--hold]
titan restore
titan guard
```
