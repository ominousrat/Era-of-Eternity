extends Node

@export var fps_counter: Label
var fps : int = 0

func _ready() -> void:
	FPSHandler.fps_counter = self.fps_counter

func _process(_delta: float) -> void:
	fps = int(Engine.get_frames_per_second())
	fps_counter.text = str("FPS: %s" % [fps])
