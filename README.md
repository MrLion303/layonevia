# EV

**EV** (pronunciado «ibi») es un asistente de escritorio ligero para Windows controlado por voz.

## Primera base

- Interfaz de escritorio moderna y ligera.
- Configuración de micrófono y salida de audio.
- Prueba de dispositivos de audio.
- Arquitectura preparada para cambiar automáticamente de dispositivo si el preferido desaparece y volver a él cuando regrese.
- Memoria persistente preparada para sincronización privada con GitHub.
- Publicación automática mediante GitHub Actions.
- Sin necesidad de Visual Studio para compilar las versiones publicadas.

## Memoria

Las memorias permanentes de EV no se deben guardar en el repositorio público del código. La sincronización está diseñada para utilizar un repositorio privado de GitHub configurado por el usuario.

Cuando no hay Internet, EV puede consultar la memoria local ya sincronizada, pero **no crea nuevas memorias permanentes** hasta recuperar la conexión.

Ejemplo:

> «EV, recuerda que siempre debes llamarme Señor.»

EV guardará esa preferencia en la memoria privada y podrá utilizarla en conversaciones posteriores.

## Compilación

El workflow **Compilar EV** publica una versión autocontenida para Windows x64 y la deja como artifact de GitHub Actions.

## Estado

Proyecto inicial v0.1.