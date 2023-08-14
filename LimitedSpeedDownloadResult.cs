using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Web;

/// <summary>
/// Represents a limited speed download result, with pause/resume capability.
/// </summary>
public class LimitedSpeedDownloadResult : IActionResult
{
	// Stores the session counts for each client IP address
	private static readonly ConcurrentDictionary<string, int> _sessionCounts = new ConcurrentDictionary<string, int> ();

	// The file path of the download
	private readonly string _filePath;

	// The download speed in kilobytes per second
	private readonly int _kBps;

	// The maximum number of concurrent sessions allowed per client IP address
	private readonly int _maxConcurrentSessions;

	/// <summary>
	/// Initializes a new instance of the <see cref="LimitedSpeedDownloadResult"/> class with the specified file path, download speed, and an optional parameter to bypass temporary link restrictions.
	/// </summary>
	/// <param name="filePath">The file path of the download.</param>
	/// <param name="kbps">The download speed in kilobytes per second.</param>
	/// <param name="bypassTempLink">A flag indicating whether to bypass temporary link restrictions. Defaults to false.</param>
	public LimitedSpeedDownloadResult (string filePath, int kbps, bool bypassTempLink = false)
	{
		_filePath = filePath.Replace ('\\', '/');
		_maxConcurrentSessions = bypassTempLink ? 2 : 1;
		_kBps = bypassTempLink ? kbps / 2 : kbps;
	}

	/// <summary>
	/// Executes the limited speed download action asynchronously.
	/// </summary>
	/// <param name="context">The action context.</param>
	/// <returns>A task that represents the asynchronous operation.</returns>
	public async Task ExecuteResultAsync (ActionContext context)
	{
		FileStream stream = new FileStream (_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		BinaryReader reader = new BinaryReader (stream);

		long fileLength = stream.Length;

		int packSize = _kBps * 128;

		// Get the client IP address
		string clientIp = context.HttpContext.Connection.RemoteIpAddress.ToString ();

		if (_sessionCounts.ContainsKey (clientIp) && _sessionCounts[clientIp] >= _maxConcurrentSessions)
		{
			// Return a 429 Too Many Requests response
			context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
			return;
		}

		// Increment the session count for the client
		_sessionCounts.AddOrUpdate (clientIp, 1, (key, value) => value + 1);

		string fileName = "SkyBlock.mp4";
		context.HttpContext.Response.Headers.Add ("Content-Disposition", $"attachment; filename={fileName}");
		context.HttpContext.Response.Headers.Add ("Accept-Ranges", "bytes");

		try
		{
			// Check if the request contains the Range header
			if (context.HttpContext.Request.Headers.ContainsKey ("Range"))
			{
				// Parse the Range header value
				string rangeHeader = context.HttpContext.Request.Headers["Range"].ToString ();

				string[] rangeValues = rangeHeader.Replace ("bytes=", "").Split ("-");

				long startByte = Convert.ToInt64 (rangeValues[0]);

				long endByte = fileLength - 1;

				if (rangeValues.Length > 1 && !string.IsNullOrEmpty (rangeValues[1]))
				{
					endByte = Convert.ToInt64 (rangeValues[1]);
				}
				// Set the response status code to 206 Partial Content
				context.HttpContext.Response.StatusCode = 206;

				// Set the Content-Range header
				context.HttpContext.Response.Headers.Add ("Content-Range", $"bytes {startByte}-{endByte}/{fileLength}");

				// Set the Content-Length header
				context.HttpContext.Response.Headers.Add ("Content-Length", (endByte - startByte + 1).ToString ());

				// Seek to the start byte in the file stream
				stream.Seek (startByte, SeekOrigin.Begin);

				// Update the packSize and packsCount values based on the requested range
				packSize = (int)Math.Min (packSize, endByte - startByte + 1);
				int packsCount = (int)Math.Ceiling ((double)(endByte - startByte + 1) / packSize);

				for (int i = 0; i < packsCount; i++)
				{
					byte[] buffer = reader.ReadBytes (packSize);
					await context.HttpContext.Response.Body.WriteAsync (buffer, 0, buffer.Length, context.HttpContext.RequestAborted);
					// Delay to control the speed
					int delay = (int)Math.Ceiling (1000.0 * buffer.Length / (_kBps * 1024)); // Calculate delay based on packet size and desired speed in kbps
					await Task.Delay (delay, context.HttpContext.RequestAborted);
				}
			}
			else
			{
				// Set the Content-Length header
				context.HttpContext.Response.Headers.Add ("Content-Length", fileLength.ToString ());
				int packsCount = (int)Math.Ceiling ((double)fileLength / packSize);

				for (int i = 0; i < packsCount; i++)
				{
					byte[] buffer = reader.ReadBytes (packSize);
					await context.HttpContext.Response.Body.WriteAsync (buffer, 0, buffer.Length, context.HttpContext.RequestAborted);
					// Delay to control the speed
					int delay = (int)Math.Ceiling (1000.0 * buffer.Length / (_kBps * 1024)); // Calculate delay based on packet size and desired speed in kbps
					await Task.Delay (delay, context.HttpContext.RequestAborted);
				}
			}

			context.HttpContext.Response.ContentType = "application/octet-stream";

			string utf8EncodingFileName = HttpUtility.UrlEncode (_filePath.Split ("/").Last (), System.Text.Encoding.UTF8);
			context.HttpContext.Response.Headers.Add ("Content-Disposition", "attachment;filename=" + utf8EncodingFileName);
		}
		catch (OperationCanceledException)
		{
			// Decrement the session count for the client when request is aborted by client.
			_sessionCounts.AddOrUpdate (clientIp, 0, (_, value) => Math.Max (0, value - 1));
		}
		finally
		{
			reader.Close ();
			stream.Close ();
		}
	}
}