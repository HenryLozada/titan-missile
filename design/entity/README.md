# La entidad — referencia de render

- `entity.frag` — fragment shader GLSL ES 3.00 (WebGL2). Raymarching de SDF en un solo pase:
  ferrofluido que se transforma entre los 5 sólidos platónicos (dodecaedro, octaedro, tetraedro,
  icosaedro, cubo) con púas, gotas metaball, magnetismo hacia el cursor, borde/halo para fondos oscuros.
- `engine.reference.js` — controlador de referencia: resortes por estado, 30 fps en reposo,
  0 fps con la ventana oculta, y los efectos de sonido sintetizados (WebAudio).

Estados: 1 Reposo `#00FF66` · 2 Análisis `#00E5FF` · 3 Max Boost `#D000FF` · 4 Liberación `#00FFAA` · 5 Persistencia `#FFB700`.

Port a la app WinUI 3: el shader se traduce a HLSL casi 1:1 (vec→float, mix→lerp, fract→frac,
mat2*v → mul) y se dibuja en un SwapChainPanel con Direct3D 11, o con Win2D PixelShaderEffect.
