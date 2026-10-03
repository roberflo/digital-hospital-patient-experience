# Funciones de Chatwoot aplicadas a Recepción

Revisión: 2026-10-02. Referencias primarias:
- https://github.com/chatwoot/chatwoot
- https://github.com/chatwoot/chatwoot/blob/develop/app/models/custom_filter.rb
- https://github.com/chatwoot/chatwoot/blob/develop/app/models/macro.rb

Se revisaron vistas por usuario/cuenta y acciones de macros. La implementación es propia para Next.js/.NET; no se incorporó código Ruby/Vue.

## Entregado

1. **Vistas personales persistentes**: guardar estado, responsable, canal, prioridad, etiqueta y modo equipo/agente. Hasta 20 por usuario; aislamiento por usuario y hospital; eliminar desde Mis vistas.
2. **Macros del hospital**: supervisores crean procedimientos de estado, prioridad, etiquetas acumulativas, asignación al ejecutor y nota interna. Todos los miembros pueden aplicarlas con vista previa. Una sola escritura atómica guarda cambios, nota e historial; revisión optimista evita repetir una acción con una versión antigua. No envían mensajes externos ni habilitan al agente.
3. **Búsqueda de mensajes**: texto y nombre de archivo dentro de los últimos 200 mensajes cargados, con límite visible. No es búsqueda global de todo el histórico.
4. **Filtro de archivos**: adjuntos y audios del chat, abiertos por el endpoint autenticado ya existente; se puede combinar con búsqueda.

Los controles usan selectores, botones y diálogos compactos; no se añadieron métricas al inbox.

## Verificación

- 49 pruebas backend, incluidos límites/validación de macros y vistas.
- 82 comprobaciones API, incluidas privacidad de vistas, tenant, permisos de macros, conflicto de revisión y nota única sin envío al paciente.
- 14 escenarios de navegador: macros verificadas en escritorio y móvil tras corregir etiquetas accesibles. La regresión posterior pasó 13 escenarios y detectó una carrera de hidratación del menú móvil; se corrigió la fecha renderizada y el estado del menú, y el escenario afectado pasó al repetirlo.
- Builds .NET/Next.js correctos, migración aplicada en base local y desde cero en la base de pruebas.

## Próximas prioridades

La conexión real de la agenda Hospital es el siguiente trabajo solicitado. Equipos con capacidad/disponibilidad, SLA y automatización de enrutamiento requieren reglas operativas del hospital; no se introdujeron reglas arbitrarias para pacientes. La reserva automática atómica/idempotente en Hospital sigue siendo distinta de sus sobrecitas manuales permitidas.
