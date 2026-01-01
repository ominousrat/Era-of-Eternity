extends Node3D

var data : Dictionary[Vector3, Color] = {}

func _ready() -> void:
	pass
	
func _unhandled_input(event: InputEvent) -> void:
	if event.is_action_pressed("ui_cancel"):
		get_tree().quit()
	
