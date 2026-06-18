# Reference Study: Godot Tiny MMO

Repository: https://github.com/SlayHorizon/godot-tiny-mmo

Use this repo as a reference, not as the base for this project.

## What It Is

`godot-tiny-mmo` is a Godot-first MMO framework/demo. It keeps client and server code in one Godot project, uses GDScript heavily, and targets fast iteration inside Godot.

Notable ideas worth studying:

- gateway/master/world server separation
- byte-packed protocol messages
- Godot client and server export presets
- account and character flow
- map instances and transitions
- AOI/interest management
- entity interpolation
- basic server-side validation

## How This Project Differs

This project is backend-first:

- standalone C#/.NET authoritative server
- LiteNetLib reliable UDP transport
- SQLite persistence now, Postgres persistence later
- Docker Compose local infrastructure
- diagnostic console client before Godot
- Godot treated as a future client, not the host for the server architecture

## How To Use It Later

Compare against it at these milestones:

- before designing gateway/master/world separation
- before implementing Godot client scenes and autoloads
- before adding AOI/grid filtering
- before adding multi-map travel
- before adding client interpolation

Do not copy its architecture blindly. Translate ideas into this repo's explicit server/client boundary.
