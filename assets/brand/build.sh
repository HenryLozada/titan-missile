NEON="#39FF6A"
defs() { cat <<EOF
    <linearGradient id="neon" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#B6FF3B"/>
      <stop offset=".5" stop-color="#39FF6A"/>
      <stop offset="1" stop-color="#00D98B"/>
    </linearGradient>
    <filter id="glow" x="-30%" y="-30%" width="160%" height="160%">
      <feGaussianBlur in="SourceGraphic" stdDeviation="$1" result="b"/>
      <feMerge><feMergeNode in="b"/><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
EOF
}
mark() { cat <<'EOF'
    <g filter="url(#glow)">
      <!-- alas de Hermes: cuchillas -->
      <g fill="url(#neon)">
        <polygon points="116,50 16,26 32,44 116,64"/>
        <polygon points="116,72 34,54 48,68 116,84"/>
        <polygon points="116,92 56,80 68,92 116,102"/>
        <polygon points="140,50 240,26 224,44 140,64"/>
        <polygon points="140,72 222,54 208,68 140,84"/>
        <polygon points="140,92 200,80 188,92 140,102"/>
        <!-- remate de la vara -->
        <polygon points="128,14 140,28 128,42 116,28"/>
        <!-- vara -->
        <polygon points="120,48 136,48 136,220 128,242 120,220"/>
      </g>
      <!-- serpientes: pistas de circuito entrelazadas -->
      <g fill="none" stroke-linejoin="miter" stroke-linecap="square">
        <path d="M100,116 V122 L156,154 V160 L100,192 V198 L144,224" stroke="#07060A" stroke-width="14"/>
        <path d="M100,116 V122 L156,154 V160 L100,192 V198 L144,224" stroke="url(#neon)" stroke-width="8"/>
        <path d="M156,116 V122 L100,154 V160 L156,192 V198 L112,224" stroke="#07060A" stroke-width="14"/>
        <path d="M156,116 V122 L100,154 V160 L156,192 V198 L112,224" stroke="url(#neon)" stroke-width="8"/>
      </g>
      <!-- cabezas (nodos) y colas (vías) -->
      <g fill="url(#neon)" stroke="#07060A" stroke-width="4" stroke-linejoin="miter">
        <polygon points="84,100 114,112 98,128 88,122"/>
        <polygon points="172,100 142,112 158,128 168,122"/>
      </g>
      <g fill="#07060A"><polygon points="96,108 104,111 98,115"/><polygon points="160,108 152,111 158,115"/></g>
      <g fill="#07060A" stroke="url(#neon)" stroke-width="5">
        <circle cx="146" cy="226" r="6"/><circle cx="110" cy="226" r="6"/>
      </g>
    </g>
EOF
}
wordmark() { cat <<EOF
      <g fill="$1">
        <polygon points="0,0 60,0 55,16 5,16"/><polygon points="22,20 38,20 38,58 30,64 22,58"/>
        <polygon points="74,0 90,0 90,58 82,64 74,58"/>
        <polygon points="104,0 164,0 159,16 109,16"/><polygon points="126,20 142,20 142,58 134,64 126,58"/>
        <polygon points="202,0 218,0 242,64 225,64 210,24 195,64 178,64"/>
        <rect x="256" y="0" width="16" height="64"/><rect x="296" y="0" width="16" height="64"/>
        <polygon points="256,0 272,0 312,64 296,64"/>
      </g>
      <polygon points="210,37 219,46 219,54 210,45 201,54 201,46" fill="url(#neon)"/>
EOF
}
open() { echo "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"$1\" width=\"$2\" height=\"$3\">"; echo '  <title>TITAN</title>'; }
{ open "0 0 256 256" 256 256; echo '  <defs>'; defs 3; echo '  </defs>'; mark; echo '</svg>'; } > titan-mark.svg
{ open "0 0 256 256" 256 256; echo '  <defs>'; defs 2.5; cat <<'EOF'
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#101512"/>
      <stop offset="1" stop-color="#050607"/>
    </linearGradient>
    <radialGradient id="halo" cx=".5" cy=".45" r=".55">
      <stop offset="0" stop-color="#39FF6A" stop-opacity=".22"/>
      <stop offset="1" stop-color="#39FF6A" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <rect width="256" height="256" rx="56" fill="url(#bg)"/>
  <rect width="256" height="256" rx="56" fill="url(#halo)"/>
  <rect x="2" y="2" width="252" height="252" rx="54" fill="none" stroke="#39FF6A" stroke-opacity=".35" stroke-width="3"/>
  <g transform="translate(32 28) scale(.75)">
EOF
  mark; echo '  </g>'; echo '</svg>'; } > titan-app-icon.svg
for v in dark light; do
  if [ $v = dark ]; then c="#EAF2EC"; else c="#0B0F0C"; fi
  { open "0 0 376 120" 376 120; echo '  <defs>'; defs 3; echo '  </defs>'
    echo '  <g transform="translate(0 0) scale(.46875)">'; mark; echo '  </g>'
    echo '  <g transform="translate(130 36) scale(.75)">'; wordmark "$c"; echo '  </g>'; echo '</svg>'; } > titan-logo-$v.svg
  { open "-4 -4 320 72" 320 72; echo '  <defs>'; defs 1.5; echo '  </defs>'; wordmark "$c"; echo '</svg>'; } > titan-wordmark-$v.svg
done

# icono simplificado para 16-32 px (bandeja, favicon)
{ open "0 0 256 256" 256 256; echo '  <defs>'; defs 0; cat <<'EOF2'
  </defs>
  <rect width="256" height="256" rx="56" fill="#07090A"/>
  <g fill="url(#neon)">
    <polygon points="112,44 10,20 30,52 112,72"/>
    <polygon points="112,86 40,70 60,98 112,110"/>
    <polygon points="144,44 246,20 226,52 144,72"/>
    <polygon points="144,86 216,70 196,98 144,110"/>
    <polygon points="112,30 144,30 144,212 128,240 112,212"/>
  </g>
  <g fill="none" stroke="#07090A" stroke-width="30"><path d="M84,124 L172,196 M172,124 L84,196"/></g>
  <g fill="none" stroke="url(#neon)" stroke-width="16"><path d="M84,124 L172,196 M172,124 L84,196"/></g>
</svg>
EOF2
} > titan-icon-small.svg
