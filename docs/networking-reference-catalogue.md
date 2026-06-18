# Networking Reference Catalogue

A depth-annotated catalogue of https://github.com/0xFA11/MultiplayerNetworkingResources,
read through the lens of **this** project: a server-authoritative, fixed-20 Hz, single-zone,
slow 2D top-down "Ultima-like" sandbox MMO in C#/.NET 8 on LiteNetLib reliable UDP, targeting
~120-150 visible players, SQLite-now/Postgres-later, no client prediction yet.

Each entry has a **verdict** scoped to *this* project, not to games in general:

- **ADOPT NOW** — directly applicable to the current single-zone prototype; do it soon.
- **ADOPT LATER** — the right technique, but gated on a measured need (usually bandwidth or CPU).
- **REFERENCE** — read for understanding/validation; little or no code follows.
- **REJECT** — good work, wrong genre/topology for this project (twitch FPS, fighting-game
  rollback, P2P/lockstep, engine-coupled). Listed so the "no" is deliberate, not an oversight.

The companion document [networking-design-plan.md](networking-design-plan.md) extrapolates a
single best-fit plan from everything below.

---

## Tier 1 — Core canon (read in depth)

These were read against primary sources (or full transcripts where the primary was a talk).
They are the load-bearing references for this server.

### Snapshot / delta / interpolation / reliability (Gaffer on Games)

- **[Snapshot Compression](https://gafferongames.com/post/snapshot_compression/)** — Bit-packing,
  quantization, and **delta-against-an-acked-baseline** (unchanged entity = 1 bit). In 2D the
  quaternion/"smallest-three" machinery is irrelevant; the transferable wins are tighter
  position bit-widths sized to actual world bounds and relative-index encoding of entity-id
  lists. **Verdict: ADOPT LATER** — the bandwidth fix once full-state snapshots strain at 150
  players; premature before a snapshot-ack baseline exists.
- **[State Synchronization](https://gafferongames.com/post/state_synchronization/)** — The
  **priority accumulator**: a per-entity float that grows each tick, sorted, packed into the
  packet budget, reset on send. **Verdict: ADOPT NOW (accumulator) / REJECT (dual-sim
  extrapolation).** The accumulator is the principled replacement for the current hard
  visible-entity cap; the velocity/extrapolation half is prediction work this game doesn't need.
- **[Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/)** — Render
  in the past, interpolate between the two snapshots straddling render time; buffer ≈ 3× the
  send interval to survive two consecutive losses. At 20 Hz that's a **~150 ms / 3-snapshot
  buffer, linear interp** (2D, slow movement — Hermite/SLERP overkill). **Verdict: ADOPT NOW
  (client side).** This is the missing half of the snapshot model.
- **[Reliability, Ordering and Congestion Avoidance over UDP](https://gafferongames.com/post/reliability_ordering_and_congestion_avoidance_over_udp/)**
  — The `sequence + ack + ack_bitfield` virtual connection. LiteNetLib already provides the
  reliability/RTT layer, so don't rebuild it — **but** the ack-bitfield idea is exactly the
  **application-level "last snapshot seq received" ack** you need to unlock delta compression.
  **Verdict: REFERENCE (transport) / ADOPT LATER (app-level snapshot ack).**

### Client/server architecture canon

- **[Fast-Paced Multiplayer (Gambetta)](http://www.gabrielgambetta.com/client-server-game-architecture.html)**
  — The 4-layer model: authoritative server, client prediction (input seq + replay),
  reconciliation, entity interpolation. **Verdict: entity interpolation ADOPT NOW; prediction +
  reconciliation ADOPT LATER (local player only, if movement ever feels laggy); lag compensation
  REJECT (no hitscan).**
- **[Source Multiplayer Networking (Valve)](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking)**
  — Decouple tick rate / snapshot rate / command rate; snapshots unreliable and
  delta-against-acked-baseline; structural changes reliable; ~100 ms interpolation delay.
  **Verdict: REFERENCE / partial ADOPT** — validates the reliable-spawn / unreliable-snapshot
  split already in the code; adopt the rate-decoupling as a concept.
- **[Quake 3 Network Model (Sanglard)](http://fabiensanglard.net/quake3/network.php)** — The
  cleanest **per-client acked-baseline delta** implementation: a 32-deep snapshot ring per
  client, diff against last-acked, field-level changed-bit + bit-width descriptors. Self-healing
  under loss (no explicit retransmit). **Verdict: ADOPT LATER** — the concrete blueprint for the
  delta step after full-state snapshots hurt.
- **[The DOOM III Network Architecture (PDF)](http://mrelusive.com/publications/papers/The-DOOM-III-Network-Architecture.pdf)**
  — Q3's successor model; snapshot + delta with refinements. **Verdict: REFERENCE** (same family
  as Quake 3; read alongside it).
- **[Replication in Networked Games, Part 1 (0fps)](https://0fps.net/2014/02/10/replication-in-networked-games-overview-part-1/)**
  — Formal active (lockstep) vs passive (client-server) taxonomy; concludes client-server is the
  right general default. **Verdict: REFERENCE** — theoretical backing for the chosen model and
  for refusing determinism/lockstep work.
- **[Game Networking Demystified (Ruoyu Sun)](https://ruoyusun.com/2019/03/28/game-networking-1.html)**
  — "State sync vs input sync"; explicit mapping **MMO → state sync, no determinism required**.
  **Verdict: REFERENCE** — cheapest framing; the "no determinism needed" point saves real effort.

### MMO-scale architecture

- **[The TRIBES Engine Networking Model (PDF)](https://www.gamedevs.org/uploads/tribes-networking-model.pdf)**
  — *The closest blueprint to this project.* Four **delivery classes** chosen per-datum; a
  **packet-notification** layer (tells you delivered/dropped, never retransmits); a per-client
  **Ghost Manager** (scope = AOI; ghost create/delete = reliable spawn/despawn); **state masks**
  (per-entity dirty bits, most-recent-state wins, no retransmit bookkeeping); **per-object
  priority** packed until the packet is full; **datablocks** (static templates sent once,
  referenced by id). **Verdict: ADOPT NOW (concepts).** This is the design the snapshot system
  should converge toward; skip only its prediction/move-determinism parts.
- **[Tech-Stack of the Original Ultima Online Servers](https://www.quora.com/What-was-the-technology-stack-driving-the-original-Ultima-Online-servers)**
  — *(primary blocked; reconstructed from UOGuide + corroboration — second-hand)* A shard =
  cluster of **subservers each owning a contiguous map region**, seamed by visible **"server
  lines."** Started monolithic, split spatially only under load; modern low-pop shards run
  single-process again. **Verdict: REFERENCE NOW (validation) / ADOPT LATER (the spatial split
  model).** The best historical *genre* analog — your eventual split should be region-based with
  entity hand-off at boundaries.
- **[Building a PvP-Focused MMO — Albion (David Salz)](https://www.youtube.com/watch?v=x_4Y2-B-THo)**
  — *(slide-backed second-hand)* Single-language C# client+server code sharing; three-layer
  client (input/sim/visualization); **live state held in server memory, DB is write-mostly**
  (Cassandra: slow reads, fast writes). **Verdict: ADOPT NOW (memory-authoritative + async
  write-behind persistence; share message contracts client↔server) / REJECT (Cassandra; client
  prediction for now).**
- **[Network Serialization and Routing in WoW — "JAM" (Joe Rumsey, GDC)](http://www.gdcvault.com/play/1017733/Network-Serialization-and-Routing-in)**
  — *(weakly sourced — slides were image-only; verify against the archive.org video)*
  Schema/codegen-driven serialization ("a robot writes the pack/unpack so humans don't add
  bugs"); **routing by entity, not address** (send to entity N; the layer finds the owning
  process); protocol version negotiation. **Verdict: ADOPT NOW (single-source serialization) /
  ADOPT LATER (entity-addressed routing as a seam you stub now as an in-process call) /
  REFERENCE (version negotiation).**
- **[Shared World Shooter: Destiny's Networked Mission Architecture (Bungie)](https://www.gdcvault.com/play/1022246/Shared-World-Shooter-Destiny-s)**
  — *(full transcript read — primary)* Hybrid P2P/cloud, "bubbles," three hosts per player —
  mostly wrong topology for this project. The one gem: **"Activity State"** — declare and
  persist only a minimal sensor-backed set of authoritative facts, separate from full
  simulation, which yielded ~50:1 server density. **Verdict: REFERENCE-ONLY, with ONE ADOPT-NOW
  concept** — split state into *transient* (in-memory, lossy) vs *durable-contract* (persisted:
  quest flags, container contents, ownership). The cost of a server is how much state you keep
  authoritative, not raw player count.
- **[1500 Archers on a 28.8 (Age of Empires)](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond)**
  — Deterministic P2P lockstep, command-sync. **Verdict: REFERENCE** — architecturally the
  *opposite* of this project; keep only the latency-tolerance numbers (<250 ms unnoticed;
  consistent beats variable) and the inverse proof that full-state replication is the costly
  path that delta/most-recent-state exists to tame.

### AOI scaling & tick budget

- **[Demolishing Wallhacks with VALORANT's Fog of War](https://technology.riotgames.com/news/demolishing-wallhacks-valorants-fog-war)**
  — *(second-hand; Riot primaries serve an expired cert to fetchers)* Server decides per-client
  what data a client may even receive — **visibility filtering is bandwidth control AND
  anti-cheat.** They use precomputed PVS, not runtime raycasts. **Verdict: ADOPT NOW (principle)
  / REJECT (PVS/raycast mechanism).** Make "outside AOI ⇒ never serialized into that client's
  packet" an invariant; a top-down sandbox is very exposed to map/radar hacks.
- **[Peeking into VALORANT's Netcode](https://technology.riotgames.com/news/peeking-valorants-netcode)**
  — *(second-hand)* Interpolation buffer is the smoothness↔latency knob; ~1 frame is the natural
  default. **Verdict: ADOPT NOW (interpolation buffer ≈ 1 tick / 50 ms) / REFERENCE (peeker's
  advantage, lag-comp — not relevant to slow movement).**
- **[VALORANT's 128-Tick Servers](https://technology.riotgames.com/news/valorants-128-tick-servers)**
  — *(second-hand; richest tick-budget source)* Derive a **hard per-tick CPU budget** from tick
  rate, **profile the tick into ~10 categories**, optimize against per-system budgets; "don't
  simulate what no one observes." **Verdict: ADOPT NOW (explicit 50 ms budget + category/drift
  profiling) / ADOPT LATER (observer-gated sim) / REFERENCE (hardware/cache specifics).** At 20 Hz
  you have ~6-20× their headroom; copy the *discipline of measuring*, not the numbers.
- **[Overwatch Gameplay Architecture and Netcode (Timothy Ford)](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)**
  — *(second-hand)* ECS as complexity control: 100+ components but only ~3 systems touch netcode.
  Predict-everything + rollback (twitch-only). **Verdict: ADOPT NOW (data-oriented
  structure-of-arrays WorldState + quarantine the netcode surface to a couple of systems) /
  REJECT (rollback, predict-everything, hit-reg rewind).**
- **[Replication Graph (Unreal)](https://www.youtube.com/watch?v=CDnNAAzgltw)** — *(second-hand)*
  Kills default O(actors × connections) relevancy via a **uniform grid**: bucket entities into
  cells, per-client gather = own cell + neighbors (list union, not pairwise distance). Cell ≈
  cull radius ⇒ 3×3 block. **Verdict: ADOPT LATER (the exact target AOI architecture)** —
  metrics-gated; at 150 entities naive O(n²) ≈ 22.5k checks/tick may still fit 50 ms.

### Transport, libraries & test tools

- **[LiteNetLib](https://github.com/RevenantX/LiteNetLib)** — The incumbent. Channels, all
  delivery methods, auto-MTU + fragmentation, NAT punch, CRC32C, **built-in latency/loss
  simulation**. **Verdict: VALIDATES CURRENT CHOICE.** Correct pick for a standalone .NET server;
  lean on per-data-type delivery methods + separate channels (state on unreliable-sequenced,
  events on reliable-ordered) so a big reliable payload never head-of-line-blocks movement.
- **[Riptide](https://github.com/tom-weiland/RiptideNetworking)** — The one genuine engine-agnostic
  C# peer. **Verdict: VALIDATES CHOICE / weak migration option.** Confirms the category; no reason
  to switch.
- **mas-bandwidth study trio** —
  [reliable](https://github.com/mas-bandwidth/reliable) (ack-bitfield over UDP),
  [serialize](https://github.com/mas-bandwidth/serialize) (bitpacking; one function for
  read/write/measure), [netcode](https://github.com/mas-bandwidth/netcode) (connect-token
  handshake). **Verdict: BORROW IDEAS** — these are the syllabus for a custom protocol:
  bitpacking, acked-delta snapshots, and (when auth arrives) short-lived signed connect tokens.
  Study, don't link (all C/C++).
- **[yojimbo](https://github.com/mas-bandwidth/yojimbo)** / **[ENet](http://enet.bespin.org/)** /
  **[KCP](https://github.com/skywind3000/kcp)** / **[GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets)**
  — **Verdict: BORROW IDEAS / migration option (weak).** ENet explains *why* LiteNetLib works as
  it does; KCP only if reliable-channel latency under loss ever bites; GNS/yojimbo are C++ and
  heavyweight.
- **[MagicOnion](https://github.com/Cysharp/MagicOnion)** — gRPC/HTTP-2 .NET RPC. **Verdict:
  migration option LATER for out-of-band services** (login, admin RPC, inter-service) — wrong
  layer for tick traffic, right tool for the eventual service split.
- **[clumsy](https://jagt.github.io/clumsy/)** + **[Wireshark](https://www.wireshark.org/)** —
  **Verdict: USE NOW (Windows).** clumsy injects real-socket latency/loss/reorder/dup that
  LiteNetLib's in-process simulator can't fully reproduce; Wireshark verifies wire size, sub-MTU
  snapshots, and that bitpacking actually pays off.
- **[Nakama](https://github.com/heroiclabs/nakama)** / **[Agones](https://github.com/googleforgames/agones)** /
  **[Quilkin](https://github.com/googleforgames/quilkin)** — **Verdict: migration option LATER.**
  Accounts/social/matchmaking (Nakama), fleet orchestration (Agones), UDP edge proxy/DDoS
  (Quilkin) — all real, all strictly post-single-process and metrics-justified.
- **Unity/engine-coupled C# (Mirror, FishNet, Netick, GONet, Forge, DarkRift, Lidgren)** —
  **Verdict: NOT RELEVANT (design reference only).** Not usable from a standalone server.
  FishNet/Netick *docs* are good free reading on tick-based prediction/interpolation for the
  future Godot client.

---

## Tier 2 — Triage tail (compact verdicts)

Everything else in the upstream list, grouped, with a one-line verdict so the catalogue is
complete and every "no" is deliberate.

### Articles

| Resource | Verdict | Why |
|---|---|---|
| [Writing Server & Network Code (Wyatt, GDC)](http://www.gdcvault.com/play/1015609/Writing-Server-and-Network-Code) | REFERENCE (high value) | Dedicated-server/MMO war stories from a practitioner; great background. |
| [Half-Life & TF Networking (Bernier, GDC 2000)](https://www.gdcvault.com/play/1016642/Half-Life-and-Team-Fortress) | REFERENCE | Foundational client-side-prediction/lag-comp talk; historical grounding. |
| [HandmadeCon 2015 / Pat Wyatt](https://www.youtube.com/watch?v=1faaOrtHJ-A) | REFERENCE | Guild Wars / Diablo / StarCraft netcode war stories. |
| [The Case of the Quake Cheats](http://www.catb.org/esr/writings/quake-cheats.html) | REFERENCE | Why you never trust the client — backs the authoritative model. |
| [Don't use Lockstep in RTS games](https://blog.istrolid.com/blog/dont-use-lockstep-in-rts-games.html) | REFERENCE | Validates the choice *not* to do lockstep/determinism. |
| [Game Server Architecture (Matthew Walker)](https://web.archive.org/web/20210419133753/https://gameserverarchitecture.com/) | REFERENCE | MMO server-architecture blog; aligned with this project's concerns. |
| [IT Hare on Network Programming](http://ithare.com/category/network-programming/) | REFERENCE | Deep, MMO-relevant series (incl. "1000273-word" netcode guide). |
| [Más Bandwidth (Fiedler)](https://mas-bandwidth.com) | REFERENCE | Scalable backend + network programming; same author as Gaffer. |
| [SnapNet blog](https://www.snapnet.dev/blog/) | REFERENCE | Netcode architecture write-ups (snapshot/rollback). |
| [Real Time Multiplayer in HTML5](http://buildnewgames.com/real-time-multiplayer/) | REFERENCE (relevant to web client) | JS authoritative-server + interpolation — maps to the Three.js debug client. |
| [High Performance Browser Networking](https://hpbn.co/) | REFERENCE (web bridge) | WebSocket/transport background for the browser client bridge. |
| [Choosing TCP or UDP (Heroic Labs)](https://web.archive.org/web/20210415231950/https://heroiclabs.com/docs/expert-tcp-udp/) | REFERENCE | Basic; decision already made (UDP). |
| [Network Protocols (Destroy All Software)](https://www.destroyallsoftware.com/compendium/network-protocols) | REFERENCE | Low-level stack primer. |
| [Netcode Explained (Battle(non)sense)](https://www.pcgamer.com/uk/netcode-explained/) | REFERENCE | Beginner-friendly concept intro. |
| [Game Networking war-story podcasts: Between Two Servers](https://www.youtube.com/playlist?list=PLzVi6Kh_HMIWKS1aXOV4XobuymoF0P-_S), [Gambetta interview](https://www.youtube.com/watch?v=HHdUUP3Z3HA) | REFERENCE | Background listening. |
| [What Makes Apex Tick](https://www.ea.com/en-au/games/apex-legends/news/servers-netcode-developer-deep-dive) | REFERENCE | FPS server/netcode deep dive; tick/CPU context. |
| [It IS Rocket Science (Rocket League)](https://www.gdcvault.com/play/1024972/It-IS-Rocket-Science-The) | REFERENCE | Physics networking; interesting, not applicable. |
| [Networking Scripted Weapons & Abilities (Overwatch)](https://www.youtube.com/watch?v=ScyZjcjTlA4) | REFERENCE | Ability-networking patterns for later gameplay. |
| Determinism in LoL; NAT Punch-through; Lag Compensation (Vercidium); Netcode at Super Bit Machine; Sync Host (Insomniac); The Poor Man's Netcode; Warframe; AC Unity AI; ELIMINATE server; Stop Copy/Paste Networking | REFERENCE | Useful but tangential to a slow authoritative top-down sandbox. |
| Fighting-game / rollback set: [Fightin' Words](http://ki.infil.net/w02-netcode.html), [GGPO / Fight the Lag](https://drive.google.com/file/d/1cV0fY8e_SC1hIFF5E1rT8XRVRzPjU8W9/view), [8 Frames in 16ms](https://www.youtube.com/watch?v=7jb0FOcImdg), [Delta Rollback](https://medium.com/@david.dehaene/delta-rollback-new-optimizations-for-rollback-netcode-7d283d56e54b), [Rollback in INVERSUS](http://blog.hypersect.com/rollback-networking-in-inversus/), [Rollback Pseudo Code](https://gist.github.com/rcmagic/f8d76bca32b5609e85ab156db38387e9), [I wanna make a fighting game](https://andrea-jens.medium.com/i-wanna-make-a-fighting-game-a-practical-guide-for-beginners-part-1-2021-update-955a4672eea5), [Explaining Delay/Rollback](https://arstechnica.com/gaming/2019/10/explaining-how-fighting-games-use-delay-based-and-rollback-netcode/) | REJECT | Peer-to-peer rollback for 2-player twitch games; wrong topology and genre. |
| Deterministic-sim set: [For Honor](https://www.gdcvault.com/play/1024949/-For-Honor-From-a) + [Back to the Future](https://gdcvault.com/play/1026077/Back-to-the-Future-Working), [Quantum Deep Dive](https://vimeo.com/335798361/2f90c04a30) | REJECT | Determinism/lockstep — explicitly not needed under state sync. |
| Engine-internal / FPS hit-reg set: [How a Shooter Shoots (BF3)](https://kotaku.com/5869564/networking-how-a-shooter-shoots), [Fighting Latency CoD](https://www.gdcvault.com/play/1023220/Fighting-Latency-on-Call-of), [I Shot You First (Halo)](http://www.gdcvault.com/play/1014345/I-Shot-You-First-Networking), [Tick-Based Lag Comp (Unity)](https://twotenpvp.github.io/lag-compensation-in-unity.html), [Crysis 2 MP](http://www.gdcvault.com/play/1014886/Crysis-2-Multiplayer-A-Programmer), [Halo Infinite](https://www.halowaypoint.com/news/closer-look-halo-infinite-online-experience), Pixonic (mobile) | REJECT | Hitscan/lag-comp/twitch concerns absent from slow server-resolved combat. |
| Unreal-specific: [UE1](https://docs.google.com/document/d/1KGLbEfHsWANTTgUqfK6rkpFYDGvnZYj-BN18sxq6LPY)/[UE3](https://api.unrealengine.com/udk/Three/ReplicationHome.html)/[UE4](https://web.archive.org/web/20230324101942/http://www.nafonso.com/home/unreal-framework-network) netcode, [Compendium](https://cedric-neukirchen.net/docs/category/multiplayer-network-compendium/), [vorixo](https://vorixo.github.io/devtricks/), [Kieran Newland](https://www.kierannewland.co.uk/blog/), Unreal Fest 2025 prediction/movement talks | REJECT | Engine-coupled replication internals; not a standalone-server reference. |
| VR / turn-based / web-P2P: [Networked Physics in VR](https://developer.oculus.com/blog/networked-physics-in-virtual-reality-networking-a-stack-of-cubes-with-unity-and-physx/), [Networking a turn-based game](https://longwelwind.net/blog/networking-turn-based-game/), [UNET HLAPI + Steam P2P](https://blog.spacewavesoftware.com/gamedev/2017-10-28-unity-unet-hlapi-and-steam-p2p-networking/), [Unity client-side prediction demo](http://www.codersblock.org/blog/client-side-prediction-in-unity-2018), [Ethernet vs WiFi](https://web.archive.org/web/20191231135556/https://na.leagueoflegends.com/en/page/ethernet-vs-wifi-ping-packets-playing-better) | REJECT | Different problem shape entirely. |
| Watch Dogs 2 vehicle replication; Replay Tech in Overwatch | REJECT | P2P vehicle / replay capture — out of scope. |

### Libraries

| Resource | Verdict | Why |
|---|---|---|
| [TNL2 (Torque)](https://github.com/nardo/tnl2) | REFERENCE | The library descendant of the TRIBES model — read alongside the Tribes paper. |
| [GoWorld](https://github.com/xiaonanln/goworld) | REFERENCE | Distributed game-server engine with spatial/region splitting + hot-swap — study for the eventual split. |
| [SmartFoxServer](http://smartfoxserver.com/), [DarkRift](https://github.com/DarkRiftNetworking/DarkRift) | REFERENCE | Standalone authoritative-server design references. |
| [RakNet](https://github.com/facebookarchive/RakNet) | REFERENCE | Historically important reliable-UDP engine; archived. |
| [Photon](https://photonengine.com), [Normcore](https://normcore.io/), [Colyseus](https://github.com/colyseus/colyseus), [Actionhero](https://actionherojs.com), [SocketCluster](https://github.com/SocketCluster/socketcluster), [Kalm](https://github.com/kalm/kalm.js), [Barebones Master Server](https://github.com/alvyxaz/barebones-masterserver), [TNet](https://assetstore.unity.com/packages/tools/network/networking-and-serialization-tools-tnet-3-56798), [NetStack](https://github.com/nxrighthere/NetStack), [Networker](https://github.com/MarkioE/Networker), [Network Next](https://github.com/networknext/next), [Proton](https://github.com/mas-bandwidth/proton), [GGPO](https://github.com/pond3r/ggpo), [BestoNet](https://github.com/BestoGames/BestoNet), Godot rollback addons | NOT RELEVANT | Cloud/SaaS, other-language, Unity-coupled, fighting-game rollback, or owns the architecture this project deliberately self-builds. |

### Tools

| Resource | Verdict | Why |
|---|---|---|
| [clumsy](https://jagt.github.io/clumsy/), [Wireshark](https://www.wireshark.org/) | USE NOW | Windows latency/loss injection + wire inspection (see Tier 1). |
| [netem](https://wiki.linuxfoundation.org/networking/netem) | LATER | Linux `tc` conditioning for server/CI hosts once deploying on Linux. |
| [mitmproxy](https://mitmproxy.org/), [Postman](https://www.postman.com/), [websocat](https://github.com/vi/websocat) | LATER (web side) | For the HTTP login/REST surface and the web client's WS bridge, not UDP gameplay. |
| [Network Link Conditioner](https://nshipster.com/network-link-conditioner/) | NOT RELEVANT | macOS/iOS only. |
| [CapAnalysis](https://www.capanalysis.net/ca/), [ns](https://www.nsnam.org), [matchmaker](https://github.com/mas-bandwidth/matchmaker) | NOT RELEVANT | Research/large-scale analysis tooling beyond current needs. |

---

## Sourcing caveats

- Gaffer, Gambetta, Quake 3, 0fps, Ruoyu Sun, the Tribes PDF, the AoE article, and the Destiny
  transcript were read from **primary** sources.
- The **Valve Source wiki** blocks automated fetch (403); its specifics were confirmed via search
  + public mirror.
- The **Riot/VALORANT** pages redirect to a host serving an expired TLS cert to fetchers; those
  three lessons come from third-party recaps — treat specific millisecond figures as second-hand.
- **Ultima Online**, **Albion**, and **WoW/JAM** are video/slide talks reconstructed from
  secondary coverage (UO Quora primary was rate-limited; JAM slides were image-only). Verify exact
  stack/figures against the primaries before quoting them as fact. JAM is the weakest-sourced.
