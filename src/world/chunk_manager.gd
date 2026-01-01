class_name ChunkManager extends Node

@export var colors : Array[Color] = []
@export var dimensions : Vector3 = Vector3(1024, 256, 1024)
@export var chunk_size : int = 16
@export var noise_seed : int = 0

var noise = FastNoiseLite.new()
var number_of_chunks : Vector3
var total_chunks : int

var chunk_pref = preload("res://scenes/chunk.tscn")

var chunks_generated : Dictionary[Vector3, bool] = {}


func _ready() -> void:
	ThreadHandler.add_threads(9)
	noise.noise_type = FastNoiseLite.TYPE_PERLIN
	noise.frequency = 0.0015
	
	number_of_chunks = dimensions / chunk_size
	
	total_chunks = number_of_chunks.x * number_of_chunks.y * number_of_chunks.z
	
	var start_time = Time.get_ticks_usec()
	
	generate_chunks(Vector3(32,8,32))
	
	var end_time = Time.get_ticks_usec()
	var delta_time = (end_time - start_time) / 1000000.0
	
	print_debug("World-Generation-Time: %ss" % [delta_time])


func generate_chunk(pos : Vector3) -> void:
	var new_chunk = chunk_pref.instantiate()
		
	new_chunk.position = chunk_size * pos
	new_chunk.generate_data(chunk_size, dimensions.y, noise, colors)
	if new_chunk.min_height < pos.y * chunk_size + chunk_size + 1:
		new_chunk.generate_mesh()
		call_deferred("add_child", new_chunk)
		#print_debug("hello")


func generate_chunks(center: Vector3) -> void:
	# 1. Build list of all chunk positions in chunk coordinates.
	var all_positions: Array[Vector3] = []
	for x in range(number_of_chunks.x):
		for y in range(number_of_chunks.y):
			for z in range(number_of_chunks.z):
				all_positions.append(Vector3(x, y, z))

	# 2. Sort positions by distance to the given center.
	#    Chunks closest to `center` are generated first, expanding outward.
	all_positions.sort_custom(func(a: Vector3, b: Vector3) -> bool:
		var da := a.distance_squared_to(center)
		var db := b.distance_squared_to(center)
		return da < db
	)

	var thread_count := ThreadHandler.worker_threads.size()
	if thread_count == 0:
		# Fallback: generate on main thread if no workers.
		for pos in all_positions:
			generate_chunk(pos)
		return

	# 3. Distribute sorted positions across threads (round-robin).
	var chunk_lists: Array = []
	chunk_lists.resize(thread_count)
	for i in range(thread_count):
		chunk_lists[i] = []

	for i in range(all_positions.size()):
		var thread_index := i % thread_count
		chunk_lists[thread_index].append(all_positions[i])

	# 4. Start each thread with its slice of positions.
	for i in range(thread_count):
		var t: Thread = ThreadHandler.worker_threads[i]
		var positions_for_thread: Array = chunk_lists[i]
		if positions_for_thread.is_empty():
			continue
		t.start(Callable(self, "_thread_generate_positions").bind(positions_for_thread))


func _thread_generate_positions(positions: Array) -> void:
	for pos in positions:
		generate_chunk(pos)


func _exit_tree() -> void:
	for thread in ThreadHandler.worker_threads:
		thread.wait_to_finish()
