"""Synthetic loopback-only TLS tunnel. Never prints request data or stores traffic."""
import socket
import ssl
import sys
import threading

context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.minimum_version = ssl.TLSVersion.TLSv1_2
context.maximum_version = ssl.TLSVersion.TLSv1_2
context.load_cert_chain(sys.argv[1], sys.argv[2])
target_port = int(sys.argv[3])


def serve(client):
    try:
        with context.wrap_socket(client, server_side=True) as secure:
            secure.settimeout(5)
            data = bytearray()
            while b"\r\n\r\n" not in data:
                block = secure.recv(4096)
                if not block or len(data) + len(block) > 16384:
                    return
                data.extend(block)
            with socket.create_connection(("127.0.0.1", target_port), timeout=5) as plain:
                plain.sendall(data)
                while block := plain.recv(4096):
                    secure.sendall(block)
    except (OSError, ssl.SSLError):
        client.close()


with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    print(listener.getsockname()[1], flush=True)
    while True:
        client, _ = listener.accept()
        threading.Thread(target=serve, args=(client,), daemon=True).start()
