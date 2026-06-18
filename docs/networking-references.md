# Multiplayer Networking References

Filtered from https://github.com/0xFA11/MultiplayerNetworkingResources for this MMO learning project. Treat this as a reading queue, not a required implementation list.

## MMO / Server Architecture

- [So, you want to build an MMORPG Server](https://wirepair.org/2023/06/29/so-you-want-to-build-an-mmorpg-server/) - Useful reality check for monolith-first scope, ECS/tooling ideas, reliable UDP choices, and how quickly MMO server scope grows.
- [Writing Server and Network Code for Your Online Game](https://www.gdcvault.com/play/1015609/Writing-Server-and-Network-Code) - Practical dedicated-server architecture talk from Patrick Wyatt.
- [Shared World Shooter: Destiny's Networked Mission Architecture](https://www.gdcvault.com/play/1022246/Shared-World-Shooter-Destiny-s) - Useful reference for shared-world zoning, overlapping network sessions, and mission simulation.
- [Network Serialization and Routing in World of Warcraft](https://www.gdcvault.com/play/1017733/Network-Serialization-and-Routing-in) - High-signal Blizzard talk on inter-server serialization and routing.
- [Building a PvP Focused MMO](https://www.youtube.com/watch?v=x_4Y2-B-THo) - Albion Online architecture talk; useful for process boundaries, object/view separation, server-farm thinking, and why those splits should stay long-term for this project.

## Interest Management / AOI

- [The TRIBES Engine Networking Model](https://www.gamedevs.org/uploads/tribes-networking-model.pdf) - Classic paper covering object scoping, partial state updates, delivery classes, and packet notification.
- [Demolishing Wallhacks with VALORANT's Fog of War](https://www.riotgames.com/en/news/demolishing-wallhacks-valorants-fog-war) - Practical example of server-side visibility filtering as both AOI and anti-cheat.
- [Replication Graph](https://www.youtube.com/watch?v=CDnNAAzgltw) - Unreal's production-oriented approach to scaling replication by spatial relevance and actor routing.

## Snapshot / Delta Compression

- [Snapshot Compression](https://gafferongames.com/post/snapshot_compression/) - Best starting point for quantization, delta baselines, ack-driven compression, and bandwidth budgeting.
- [State Synchronization](https://gafferongames.com/post/state_synchronization/) - Explains when to synchronize state instead of inputs and how to prioritize bandwidth.
- [Quake 3 Network Model](https://fabiensanglard.net/quake3/network.php) - Source-level walkthrough of snapshots, deltas, commands, and server-client packet flow.
- [Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking) - Practical reference for ticks, snapshots, interpolation delay, prediction, and server reconciliation.

## Client Prediction / Interpolation

- [Fast-Paced Multiplayer](https://www.gabrielgambetta.com/client-server-game-architecture.html) - Clear practical series on authoritative servers, prediction, reconciliation, interpolation, and lag compensation.
- [Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/) - Concrete implementation model for smoothing remote entities without simulating them locally.
- [Peeking into VALORANT's Netcode](https://www.riotgames.com/en/news/peeking-valorants-netcode) - Production explanation of prediction, server authority, simulation divergence, and fairness tradeoffs.

## UDP / Reliability

- [Reliability and Congestion Avoidance over UDP](https://gafferongames.com/post/reliability_ordering_and_congestion_avoidance_over_udp/) - Core reading for sequence numbers, acks, packet loss handling, and avoiding TCP head-of-line blocking.
- [netcode](https://github.com/mas-bandwidth/netcode) - Secure UDP client/server protocol library; useful reference for handshakes, encryption, and replay protection.
- [yojimbo](https://github.com/mas-bandwidth/yojimbo) - Reliable-UDP library for dedicated-server games with channels and message delivery patterns.

## Scalability / Load Testing

- [1500 Archers on a 28.8](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond) - Classic bandwidth-budgeting and multiplayer simulation constraints story.
- [VALORANT's 128-Tick Servers](https://www.riotgames.com/en/news/valorants-128-tick-servers) - Server performance case study covering CPU budgets, frame time, hardware tuning, and scale economics.
- [Agones](https://github.com/agones-dev/agones) - Kubernetes-based dedicated game server orchestration, relevant once fleet management matters.
- [netem](https://wiki.linuxfoundation.org/networking/netem) - Linux tool for testing latency, jitter, loss, duplication, and reordering.

## Security / Cheating

- [The Case of the Quake Cheats](https://www.catb.org/~esr/writings/quake-cheats.html) - Classic explanation of why trusting clients breaks competitive multiplayer.
- [Client-Server Game Architecture](https://www.gabrielgambetta.com/client-server-game-architecture.html) - Concise explanation of authoritative servers and why clients should send intentions, not truth.
- [Quilkin](https://github.com/EmbarkStudios/quilkin) - UDP proxy for dedicated game server fleets with security, access control, telemetry, and routing concerns.
