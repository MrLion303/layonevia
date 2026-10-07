# EV

EV es un asistente de escritorio para Windows pensado para funcionar por voz y controlar tareas del sistema.

## Memoria entre PCs

La memoria permanente está diseñada para no depender de una sola instalación.

- La copia de trabajo se guarda localmente en %LOCALAPPDATA%/EV.
- La memoria sincronizada se guarda en un repositorio privado de GitHub.
- La cuenta de GitHub se vincula mediante OAuth Device Flow.
- El token de acceso se protege con DPAPI de Windows y no se guarda en texto plano.
- En otra PC, al vincular la misma cuenta, EV puede recuperar la memoria sincronizada.
- Sin conexión, EV conserva la última memoria sincronizada.
- La resolución de conflictos usa la versión con UpdatedAt más reciente.

## Configuración inicial de la memoria

1. Crea un repositorio privado llamado layonevia-memory en la cuenta de GitHub que usará EV.
2. Registra una aplicación OAuth de GitHub para EV y habilita Device Flow.
3. Coloca el Client ID de esa aplicación en EV/Services/GitHubOAuthSettings.cs.
4. Compila EV y abre la sección Memoria.
5. Pulsa Vincular GitHub y autoriza la aplicación.
6. EV usará MrLion303/layonevia-memory/memory.json para la memoria.

El Client ID puede formar parte de la aplicación; los tokens de usuario no deben incluirse en el código ni en el repositorio.

## Compilación

El proyecto se compila mediante GitHub Actions para Windows x64.

El flujo se encuentra en .github/workflows/compilar.yml.
