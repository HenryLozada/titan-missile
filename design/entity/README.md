# La entidad — referencia de render

- `entity.frag` — fragment shader GLSL ES 3.00 (WebGL2). Raymarching de SDF en un solo pase:
  ferrofluido que se transforma entre los 5 sólidos platónicos (dodecaedro, octaedro, tetraedro,
  icosaedro, cubo) con púas, gotas metaball, magnetismo hacia el cursor, borde/halo para fondos oscuros.
- `engine.reference.js` — controlador de referencia: resortes por estado, 30 fps en reposo,
  0 fps con la ventana oculta, y los efectos de sonido sintetizados (WebAudio).

Estados (intensidad Contenida, por defecto): 1 Reposo/dodecaedro `#B4E3C8` · 2 Análisis/octaedro `#B2E4EC` · 3 Max Boost/tetraedro `#DDB3EE` · 4 Liberación/icosaedro `#B3E6D6` · 5 Persistencia/cubo `#EDD5A6`.
Intensidad Viva: `#00FF66` · `#00E5FF` · `#D000FF` · `#00FFAA` · `#FFB700`.

La luz vive solo en aristas (y en los 4 vértices del tetraedro). Los sólidos son casi puros; se funden en líquido solo durante la transición.
Tipografía: Syne 800 (títulos, mayúsculas, tracking amplio) + JetBrains Mono (todo lo demás). Fondo clínico `#EEEFEC`.

Port a la app WinUI 3: el shader se traduce a HLSL casi 1:1 (vec→float, mix→lerp, fract→frac,
mat2*v → mul) y se dibuja en un SwapChainPanel con Direct3D 11, o con Win2D PixelShaderEffect.
