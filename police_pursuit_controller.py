import mmap
import struct
import time

PHYSICS_MAP = "Local\\acpmf_physics"
GRAPHICS_MAP = "Local\\acpmf_graphics"

print("==========================================================")
print("  Python Native AI Pursuit - REAL-TIME PUSH SCANNER")
print("==========================================================")

def get_shared_memory():
    while True:
        try:
            shm_phys = mmap.mmap(-1, 800, PHYSICS_MAP, access=mmap.ACCESS_READ)
            shm_graph = mmap.mmap(-1, 2000, GRAPHICS_MAP, access=mmap.ACCESS_READ)
            print("🚨 [SUCCESS] Connected to Assetto Corsa Memory Pipeline!")
            return shm_phys, shm_graph
        except Exception as e:
            print(f"[DEBUG] Waiting for Assetto Corsa session to start... ({e})")
            time.sleep(2)

shm_phys, shm_graph = get_shared_memory()

# Store initial snapshot of memory
shm_graph.seek(0)
prev_graph = list(shm_graph.read(2000))

shm_phys.seek(0)
prev_phys = list(shm_phys.read(800))

print("\n🔍 REAL-TIME PUSH SCANNER ACTIVE!")
print("👉 PUSH THE DODGE CHARGER WITH YOUR CAR NOW! Terminal will highlight the exact memory offsets that change as he moves!")

try:
    while True:
        time.sleep(0.2)

        shm_graph.seek(0)
        curr_graph = list(shm_graph.read(2000))

        shm_phys.seek(0)
        curr_phys = list(shm_phys.read(800))

        changed_graph = []
        for i in range(0, len(curr_graph) - 12, 4):
            # Check for changing 3D float vectors
            try:
                x1, y1, z1 = struct.unpack_from("fff", bytes(prev_graph), i)
                x2, y2, z2 = struct.unpack_from("fff", bytes(curr_graph), i)
                if abs(x2 - x1) > 0.05 or abs(z2 - z1) > 0.05:
                    if -5000 < x2 < 5000 and -5000 < z2 < 5000:
                        changed_graph.append((i, x2, y2, z2))
            except Exception:
                pass

        if changed_graph:
            print("\n🚨 [MOTION DETECTED!] Live changing car offsets in graphics memory:")
            for offset, x, y, z in changed_graph[:5]:
                print(f"   📍 [Offset {offset}] NEW POS: ({x:.2f}, {y:.2f}, {z:.2f})")
            prev_graph = curr_graph

except KeyboardInterrupt:
    print("Stopping Push Scanner.")
    shm_phys.close()
    shm_graph.close()
