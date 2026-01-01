extends Node

var worker_threads : Array[Thread] = []
var max_system_threads : int = 0

func add_threads(number_of_threads : int) -> void:
	worker_threads.clear()
	max_system_threads = OS.get_processor_count()
	
	if number_of_threads > max_system_threads - 3:
		number_of_threads = max_system_threads - 3
	for thread in range(number_of_threads - 3): # do not assign all threads, leave 3 for other operations
		worker_threads.append(Thread.new())
	print_debug("Amount of Threads in System: %s" % [max_system_threads])
