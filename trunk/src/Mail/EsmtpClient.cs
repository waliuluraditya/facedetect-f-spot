//
// FSpot.EsmtpClient.cs
//
// Author(s):
//   Per Arneng <pt99par@student.bth.se>
//   Sanjay Gupta <gsanjay@novell.com>
//   (C) 2004, Novell, Inc. (http://www.novell.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Net;
using System.IO;
using System.Text;
using System.Collections;
using System.Net.Sockets;
using System.Security.Permissions;
using System.Web.Mail;
using Mono.Security.Protocol.Tls;

namespace FSpot.Mail {

    /// represents a conntection to a smtp server
    internal class EsmtpClient {
	
	private string server;
        private string username;
	private string password;
	private bool use_ssl;
	private TcpClient tcpConnection;
	private EsmtpStream smtp;
	
	//Initialise the variables and connect
	public EsmtpClient( string server, string username, string password, bool use_ssl) {	    
	    this.server = server;
	    this.username = username;
	    this.password = password;
	    this.use_ssl = use_ssl;
	    Connect();
	}
	
	// make the actual connection
	// and HELO handshaking
	private void Connect() {
	    Stream stream;
	    if (use_ssl) {
		tcpConnection = new TcpClient( server , 465 );
		stream = new SslClientStream (tcpConnection.GetStream(), server, false);
	    } else {
		tcpConnection = new TcpClient( server , 25 );
		stream = tcpConnection.GetStream();
	    }
	    smtp = new EsmtpStream( stream );
	    
	    // read the server greeting
	    smtp.ReadResponse();
	    smtp.CheckForStatusCode( 220 );
	   
	    // write the HELO command to the server
	    smtp.WriteHelo( Dns.GetHostName() );
	    smtp.WriteAuth( username, password );
	}
	
	public void Send( MailMessageWrapper msg ) {
	    
	    if( msg.From == null ) {
		throw new SmtpException( "From property must be set." );
	    }

	    if( msg.To == null ) {
		if( msg.To.Count < 1 ) throw new SmtpException( "Atleast one recipient must be set." );
	    }
	    
	    	    
	    // start with a reset incase old data
	    // is present at the server in this session
	    smtp.WriteRset();
	    
	    // write the mail from command
	    smtp.WriteMailFrom( msg.From.Address );
	    
	    // write the rcpt to command for the To addresses
	    foreach( MailAddress addr in msg.To ) {
		smtp.WriteRcptTo( addr.Address );
	    }

	    // write the rcpt to command for the Cc addresses
	    foreach( MailAddress addr in msg.Cc ) {
		smtp.WriteRcptTo( addr.Address );
	    }

	    // write the rcpt to command for the Bcc addresses
	    foreach( MailAddress addr in msg.Bcc ) {
		smtp.WriteRcptTo( addr.Address );
	    }
	    
	    // write the data command and then
	    // send the email
	    smtp.WriteData();
		
	    if( msg.Attachments.Count == 0 ) {
		SendSinglepartMail( msg );	    
	    } else {
		
		SendMultipartMail( msg );
	    
	    }

	    // write the data end tag "."
	    smtp.WriteDataEndTag();

	}
	
	// sends a single part mail to the server
	private void SendSinglepartMail( MailMessageWrapper msg ) {
	    	    	    
	    // write the header
	    smtp.WriteHeader( msg.Header );
	    
	    // send the mail body
	    smtp.WriteBytes( msg.BodyEncoding.GetBytes( msg.Body ) );

	}

	// SECURITY-FIXME: lower assertion with imperative asserts	
	[FileIOPermission (SecurityAction.Assert, Unrestricted = true)]
	// sends a multipart mail to the server
	private void SendMultipartMail( MailMessageWrapper msg ) {
	    	    
	    // generate the boundary between attachments
	    string boundary = MailUtil.GenerateBoundary();
		
	    // set the Content-Type header to multipart/mixed
	    string bodyContentType = msg.Header.ContentType;

	    msg.Header.ContentType = 
		System.String.Format( "multipart/mixed;\r\n   boundary={0}" , boundary );
		
	    // write the header
	    smtp.WriteHeader( msg.Header );
		
	    // write the first part text part
	    // before the attachments
	    smtp.WriteBoundary( boundary );
		
	    MailHeader partHeader = new MailHeader();
	    partHeader.ContentType = bodyContentType;		

	    smtp.WriteHeader( partHeader );
	  
	    // FIXME: probably need to use QP or Base64 on everything higher
	    // then 8-bit .. like utf-16
	    smtp.WriteBytes( msg.BodyEncoding.GetBytes( msg.Body )  );

	    smtp.WriteBoundary( boundary );

	    // now start to write the attachments
	    
	    for( int i=0; i< msg.Attachments.Count ; i++ ) {
		MailAttachment a = (MailAttachment)msg.Attachments[ i ];
			
		FileInfo fileInfo = new FileInfo( a.Filename );

		MailHeader aHeader = new MailHeader();
		
		aHeader.ContentType = 
		    String.Format (MimeTypes.GetMimeType (fileInfo.Name) + "; name=\"{0}\"",fileInfo.Name);
		
		aHeader.ContentDisposition = 
		    String.Format( "attachment; filename=\"{0}\"" , fileInfo.Name );
		
		aHeader.ContentTransferEncoding = a.Encoding.ToString();
		    		
		smtp.WriteHeader( aHeader );
		   
		// perform the actual writing of the file.
		// read from the file stream and write to the tcp stream
		FileStream ins = fileInfo.OpenRead ();
		
		// create an apropriate encoder
		IAttachmentEncoder encoder;
		if( a.Encoding == MailEncoding.UUEncode ) {
		    encoder = new UUAttachmentEncoder( 644 , fileInfo.Name  );
		} else {
		    encoder = new Base64AttachmentEncoder();
		}
		
		encoder.EncodeStream( ins , smtp.Stream );
		
		ins.Close();
		
		    
		smtp.WriteLine( "" );
		
		// if it is the last attachment write
		// the final boundary otherwise write
		// a normal one.
		if( i < (msg.Attachments.Count - 1) ) { 
		    smtp.WriteBoundary( boundary );
		} else {
		    smtp.WriteFinalBoundary( boundary );
		}
		    
	    }
	       
	}
	
	// send quit command and
	// closes the connection
	public void Close() {
	    
	    smtp.WriteQuit();
	    tcpConnection.Close();
	
	}
	
		
    }

}
