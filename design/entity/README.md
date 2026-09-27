# La entidad — referencia de render

- `entity.frag` — fragment shader GLSL ES 3.00 (WebGL2). Raymarching de SDF en un solo pase:
  ferrofluido que se transforma entre los 5 sólidos platónicos (dodecaedro, octaedro, tetraedro,
  icosaedro, cubo) con púas, gotas metaball, magnetismo hacia el cursor, borde/halo para fondos oscuros.
- `engine.reference.js` — controlador de referencia: resortes por estado, 30 fps en reposo,
  0 fps con la ventana oculta, y los efectos de sonido sintetizados (WebAudio).

Paleta: negro, amarillo ferro `#F2C200` y blanco `#F6F6F3`. La luz vive solo en las aristas; el tetraedro (Max Boost) enciende además sus 4 vértices.
Textura ferro: micro-puntos en relieve (celular, nunca cuadrados) que brillan con la luz.
Bordes del lienzo con desvanecido: nada se corta.

- `helix.frag` — medidor de RAM: doble hélice vertical de ferrofluido. 24 peldaños ≈ 1 GB cada uno; de abajo arriba: en uso (negro), Standby (blanco), libre (amarillo).

Tipografía: Syne 800 (títulos) + JetBrains Mono (resto).

Port a la app WinUI 3: el shader se traduce a HLSL casi 1:1 (vec→float, mix→lerp, fract→frac,
mat2*v → mul) y se dibuja en un SwapChainPanel con Direct3D 11, o con Win2D PixelShaderEffect.

## v6
- Corona de eclipse detrás de la silueta (marfil → oro → rescoldo carmesí tenue): "ángel demoníaco". Máxima en Max Boost.
- 16 gotas en lágrima (cabeza + estela) en órbitas inclinadas; en la hélice, 18 gotas nacen de las hebras, se desprenden y se consumen.
- `gamepad.reference.js` — modo Xbox: stick izq./cruceta = foco espacial, A elegir, B atrás, X limpiar, Y boost,
  LB/RB secciones, stick der. = magnetismo, LT atrae / RT tensa (con vibración en gatillos), LT+RT sostenidos = descarga.
  En la app WinUI 3: navegación XY nativa de WinUI + Windows.Gaming.Input (Gamepad.Vibration.LeftTrigger/RightTrigger).
