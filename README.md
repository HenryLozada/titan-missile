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
titan boost --game cs2.exe [--hold] [--equilibrado]
titan restore
titan guard
```

## Qué hace el BOOST (todo reversible, todo en el journal)
| Paso | LÍMITE (por defecto) | EQUILIBRADO |
|---|---|---|
| Energía | TITAN Ultimate afinado: sin aparcar núcleos, turbo agresivo, EPP rendimiento, USB/PCIe/discos despiertos | igual |
| Servicios | SysMain, WSearch, DiagTrack, Spooler, MapsBroker + Windows Update, BITS, DoSvc, WerSvc, PcaSvc (solo stop/start) | los 5 primeros |
| Juego (y todo lo que arranca desde su carpeta) | CPU alta, CPU sets a P-cores / V-Cache, GPU alta (D3DKMT), sin EcoQoS ni temporizador ignorado | igual |
| Segundo plano | congelar / moderar (BelowNormal + EcoQoS), E/S baja, prioridad de memoria muy baja | sin E/S baja |
| Planificador | MMCSS; temporizador 0,5 ms solo si `GlobalTimerResolutionRequests` está activo (si no, no llegaría al juego) | igual |
| Memoria | recorta las apps de fondo → purga Standby prioridad 0 → Modificada + Standby completa | sin Modificada |

**Gobernador** (cada 2 s mientras juegas): prioriza procesos nuevos del juego, doma apps de fondo que se abran, reintenta la
prioridad de GPU hasta que el juego crea su dispositivo gráfico, y limpia memoria por niveles con umbrales según tu RAM:
*quirúrgica* (solo apps de fondo; la caché del juego no se toca) y *completa* estilo ISLC solo si la memoria libre se agota.
Voz y música (Discord, Spotify, Teams…) se moderan pero nunca se les recorta memoria.

## App (WinUI 3)
`src/Titan.App` — la entidad renderizada en Direct3D 11 (shaders en `src/Titan.App/Shaders`), motor integrado,
modo Xbox (Windows.Gaming.Input, vibración en gatillos). Al abrirse pide permisos de administrador (UAC);
si se rechazan, funciona en modo solo lectura.
```bash
dotnet build src/Titan.App
```
Ejecutable: `src/Titan.App/bin/Debug/net10.0-windows10.0.22621.0/win-x64/TITAN.exe` (`--no-elevate` para modo solo lectura).
