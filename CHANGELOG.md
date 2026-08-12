# Registro de cambios

Este proyecto usa [versionado semántico](https://semver.org/lang/es/).

## [0.1.0] — 2026-08-12

Primera versión. El servicio de monitoreo y el Viewer funcionan y están verificados contra ARCA
real; falta el instalador y la recarga en caliente de endpoints.

### Monitoreo

- Cuatro tipos de chequeo: `Http`, `Wsdl`, `SoapDummy` y `Tcp`.
- **`SoapDummy`** lee el estado de los subsistemas que informa el método dummy del servicio
  (`AppServer`, `DbServer`, `AuthServer` en el caso de ARCA). Es el único chequeo que distingue
  "el endpoint contesta 200 pero su base de datos está caída" de "el servicio anda", una falla que
  cualquier chequeo de disponibilidad común reporta en verde.
- Perfiles de servicio en `profiles.json`, como datos y no como código: sumar un servicio nuevo no
  requiere recompilar.
- Perfil de ARCA precargado: WSFEv1 y WSAA, producción y homologación.
- Clasificación de fallas: DNS, conexión rechazada, TLS, timeout, error HTTP, contenido inesperado
  y servicio informando estar caído.
- Canario como compuerta: sin internet no se alerta, no se abre incidente y no se descuenta
  disponibilidad. Un corte local no puede disfrazarse de caída del servicio remoto.
- Confirmación por conteo de intentos consecutivos, no por tiempo transcurrido.
- Ventanas de mantenimiento, aviso por vencimiento del certificado TLS, y jitter en el intervalo
  para que las instalaciones no golpeen todas en el mismo instante.

### Alertas

- Mail al caer, recordatorio periódico y aviso de recuperación, agrupados en un solo mensaje por
  incidente.
- Contraseña SMTP cifrada con DPAPI en scope `LocalMachine`, atada a la máquina que la generó.
- Tope de mails por hora, para que un endpoint inestable no queme la casilla de envío.

### Interfaz

- Viewer con semáforo, gráfico de latencia de 24 h, historial de incidentes e ícono de bandeja que
  cambia de color.
- Marcas verticales en el gráfico por severidad: amarillo para respuestas lentas, naranja para
  fallos aislados y rojo para los que formaron parte de un incidente confirmado.
- Estado amarillo por **fallas intermitentes**: un endpoint que falla de a ratos sin llegar al
  umbral de confirmación no se muestra en verde.
- Detecta que el monitor dejó de correr en vez de seguir mostrando el último estado conocido.

### Herramientas

- `--once`, `--list`, `--test <id>`, `--history [horas]`, `--test-mail` y `--protect-password`.
- `tools/FakeArca`: simulador que reproduce el escenario que no se puede provocar de otra forma —
  el servicio contestando 200 mientras informa que su base está caída. Se maneja por teclado o por
  HTTP, así que sirve para demostrar y para automatizar.
- Scripts de publicación y de alta/baja del servicio de Windows.

### Verificado contra ARCA real

El monitor capturó una caída de producción de WSFEv1 el 2026-08-12: dejó de responder a las
14:53:40 y volvió 56 segundos después, con dos chequeos en timeout, mientras WSAA seguía al 100 %.
De esa medición salió bajar `FailuresToAlert` de 3 a 2: con intervalo de 30 segundos, un corte de
esa duración produce exactamente dos chequeos fallidos, y con el valor anterior el incidente no se
habría confirmado.

### Limitaciones conocidas

- **Los cambios de endpoints no se releen en caliente.** Los destinatarios sí; agregar o cambiar
  una URL todavía exige reiniciar el servicio.
- **No hay instalador.** El alta se hace con los scripts de `scripts/`, que requieren consola
  elevada.
- El envío de mail está verificado, pero contra un solo servidor SMTP.
