import threading
import socketserver
import http.server
import random
import string
import logging

port = 80

class CustomHandler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
            
        if self.path in ["/a.js","/b.js"]:
            super().do_GET()

        else:
            if "/%3E" in self.path:
                #print(f"> {self.path}")
                s = self.path
                out = s[s.find("/%3E") + len("/%3E"):]
                print(f"> {out}")

            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.end_headers()
            self.wfile.write(b"")

    def log_message(self, format, *args):
        pass

def run_server():
    with socketserver.TCPServer(("",port), CustomHandler) as httpd:
        print(f"serving port {port}")
        httpd.serve_forever()

def save_to_a_js():
    while True:
        user_input = input("> ")
        with open("a.js", "w") as f:
            f.write(user_input)
        #print("Content saved to a.js")
        generate_random_b_js()

def generate_random_b_js():
    print("Content saved to a.bs")
    random_input = "".join(random.choices(string.digits, k=16))
    with open("b.js", "w") as f:
        f.write(random_input)
    #print("Content saved to a.bs")

if __name__ == "__main__":
    server_thread = threading.Thread(target=run_server)
    server_thread.daemon = True
    server_thread.start()

    save_to_a_js()
    #generate_random_b_js()
