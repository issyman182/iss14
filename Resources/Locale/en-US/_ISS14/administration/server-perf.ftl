cmd-serverperf-desc = Opens the server performance panel.
cmd-serverperf-help = Usage: serverperf
cmd-serverperf-server = This command cannot be used from the server console.

server-perf-title = Server Performance

server-perf-status-healthy = Healthy
server-perf-status-struggling = Struggling — occasional lag spikes
server-perf-status-overloaded = Overloaded — the server cannot keep up

server-perf-tps = TPS
server-perf-tps-value = { $actual } / { $target }
server-perf-tick-time = Tick time
server-perf-tick-time-value = { $avg } ms (max { $max } / budget { $budget })
server-perf-late-ticks = Late ticks (1 min)
server-perf-cpu = CPU
server-perf-cpu-value = { $cores } cores ({ $percent }% total)
server-perf-memory = Memory
server-perf-memory-value = { $working } ({ $managed } managed)
server-perf-gc = GC (1 min)
server-perf-gc-value = { $gen0 }/{ $gen1 }/{ $gen2 } — pause { $pause }%
server-perf-entities = Entities
server-perf-players = Players
server-perf-net = Net up / down
server-perf-net-value = { $up }/s / { $down }/s
server-perf-uptime = Uptime

server-perf-graph-tick = Tick time (last 2 minutes)
server-perf-graph-tick-legend = green: average per second · red: worst per second · yellow line: tick budget
server-perf-graph-cpu = CPU cores used (last 2 minutes)
server-perf-graph-cpu-legend = blue: cores of processor time per second · yellow line: one full core
