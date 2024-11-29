from flask import Flask, request
import threading
import random
import string
import logging

print("[+] urlserver starting")

app = Flask(__name__)
# Disable the default Flask logger 
log = logging.getLogger('werkzeug') 
log.setLevel(logging.ERROR)

# Function to generate a random 16-digit code
def generate_code():
    return ''.join(random.choices(string.digits, k=16))

@app.route('/<path:input_data>', methods=['GET'])
def handle_input(input_data):
    # Only print if the path contains "/>"
    if "/>" in request.full_path:
        #print(f"Input Received: {request.full_path}")
        print(f"\n{request.full_path}")
    return "Input processed."

@app.route('/a.js')
def serve_a_js():
    # Return the content of a.js
    with open('a.js', 'r') as a_file:
        content = a_file.read()
    return content

@app.route('/b.js')
def serve_b_js():
    # Return the content of b.js
    with open('b.js', 'r') as b_file:
        content = b_file.read()
    return content

def accept_input():
    while True:
        user_input = input("> ")
        with open('a.js', 'w') as a_file:
            a_file.write(user_input)
        
        random_code = generate_code()
        with open('b.js', 'w') as b_file:
            b_file.write(random_code)
        #print(f"Random code saved to b.js: {random_code}")

if __name__ == '__main__':
    # Start the input thread
    input_thread = threading.Thread(target=accept_input)
    input_thread.daemon = True
    input_thread.start()
    
    # Run the Flask app
    app.run(debug=False)
